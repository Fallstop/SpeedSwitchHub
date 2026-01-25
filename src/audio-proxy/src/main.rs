//! Audio Proxy - Low-latency audio forwarding for games that don't support device switching
//!
//! This proxy captures audio from a virtual device (e.g., VB-Cable) and plays it to
//! the actual output device. When the user switches devices, only the proxy's output
//! target changes - the game continues outputting to the same virtual device.
//!
//! Microphone proxy support: Captures from physical mic and renders to VB-Cable Input
//! so that apps capturing from VB-Cable Output get the audio.

mod audio_stream;
mod ipc;
mod ring_buffer;

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, RwLock};
use std::thread;
use std::time::Duration;

use anyhow::{Context, Result};
use log::{error, info, warn};
use windows::Win32::System::Com::{CoInitializeEx, CoUninitialize, COINIT_MULTITHREADED};

use audio_stream::{CaptureStream, RenderStream};
use ipc::{IpcCommand, IpcServer};
use ring_buffer::AudioRingBuffer;

/// Default buffer size in milliseconds
const DEFAULT_BUFFER_MS: u32 = 10;

/// Sample rate (48kHz is standard for gaming)
const SAMPLE_RATE: u32 = 48000;

/// Number of channels (stereo)
const CHANNELS: u16 = 2;

/// Parsed command line arguments
struct Args {
    speaker_in: String,
    speaker_out: String,
    mic_in: Option<String>,
    mic_out: Option<String>,
    buffer_ms: u32,
}

fn main() -> Result<()> {
    env_logger::Builder::from_env(env_logger::Env::default().default_filter_or("info")).init();

    let args = match parse_args() {
        Ok(args) => args,
        Err(e) => {
            eprintln!("Error: {}", e);
            eprintln!();
            print_usage();
            std::process::exit(1);
        }
    };

    info!("Audio Proxy starting...");
    info!("  Speaker input:  {}", args.speaker_in);
    info!("  Speaker output: {}", args.speaker_out);
    if let Some(ref mic_in) = args.mic_in {
        info!("  Mic input:      {}", mic_in);
    }
    if let Some(ref mic_out) = args.mic_out {
        info!("  Mic output:     {}", mic_out);
    }
    info!("  Buffer size:    {}ms", args.buffer_ms);

    // Initialize COM for this thread
    unsafe {
        CoInitializeEx(None, COINIT_MULTITHREADED).ok().context("Failed to initialize COM")?;
    }

    let result = run_proxy(&args);

    unsafe {
        CoUninitialize();
    }

    result
}

fn print_usage() {
    eprintln!("Usage: audio-proxy --speaker-in <id> --speaker-out <id> [--mic-in <id>] [--mic-out <id>] [--buffer <ms>]");
    eprintln!();
    eprintln!("Arguments:");
    eprintln!("  --speaker-in <id>   ID of the virtual audio device for speaker capture (e.g., VB-Cable Output)");
    eprintln!("  --speaker-out <id>  ID of the real output device for speaker playback");
    eprintln!("  --mic-in <id>       ID of the physical microphone for mic capture (optional)");
    eprintln!("  --mic-out <id>      ID of the virtual input device for mic output (e.g., VB-Cable Input)");
    eprintln!("  --buffer <ms>       Buffer size in milliseconds (default: 10)");
    eprintln!();
    eprintln!("Legacy usage (deprecated):");
    eprintln!("  audio-proxy <input_device_id> <output_device_id> [buffer_ms]");
}

fn parse_args() -> Result<Args> {
    let args: Vec<String> = std::env::args().collect();

    // Check for legacy positional arguments (backwards compatibility)
    if args.len() >= 3 && !args[1].starts_with("--") {
        let buffer_ms = args.get(3).and_then(|s| s.parse().ok()).unwrap_or(DEFAULT_BUFFER_MS);
        return Ok(Args {
            speaker_in: args[1].clone(),
            speaker_out: args[2].clone(),
            mic_in: None,
            mic_out: None,
            buffer_ms,
        });
    }

    // Parse named arguments
    let mut speaker_in: Option<String> = None;
    let mut speaker_out: Option<String> = None;
    let mut mic_in: Option<String> = None;
    let mut mic_out: Option<String> = None;
    let mut buffer_ms = DEFAULT_BUFFER_MS;

    let mut i = 1;
    while i < args.len() {
        match args[i].as_str() {
            "--speaker-in" => {
                i += 1;
                speaker_in = args.get(i).cloned();
            }
            "--speaker-out" => {
                i += 1;
                speaker_out = args.get(i).cloned();
            }
            "--mic-in" => {
                i += 1;
                mic_in = args.get(i).cloned();
            }
            "--mic-out" => {
                i += 1;
                mic_out = args.get(i).cloned();
            }
            "--buffer" => {
                i += 1;
                if let Some(val) = args.get(i) {
                    buffer_ms = val.parse().unwrap_or(DEFAULT_BUFFER_MS);
                }
            }
            "--help" | "-h" => {
                print_usage();
                std::process::exit(0);
            }
            _ => {
                return Err(anyhow::anyhow!("Unknown argument: {}", args[i]));
            }
        }
        i += 1;
    }

    let speaker_in = speaker_in.ok_or_else(|| anyhow::anyhow!("Missing required argument: --speaker-in"))?;
    let speaker_out = speaker_out.ok_or_else(|| anyhow::anyhow!("Missing required argument: --speaker-out"))?;

    Ok(Args {
        speaker_in,
        speaker_out,
        mic_in,
        mic_out,
        buffer_ms,
    })
}

/// Shared state for microphone proxy
struct MicState {
    /// Ring buffer for mic audio data
    buffer: Arc<AudioRingBuffer>,
    /// Current mic input device ID (hot-swappable)
    input_id: Arc<RwLock<String>>,
    /// Fixed mic output device ID (VB-Cable Input)
    output_id: String,
    /// Whether mic proxy is enabled
    enabled: Arc<AtomicBool>,
}

fn run_proxy(args: &Args) -> Result<()> {
    let running = Arc::new(AtomicBool::new(true));
    let running_clone = running.clone();

    // Set up Ctrl+C handler
    ctrlc_handler(running.clone());

    // Calculate buffer size in samples
    let buffer_samples = (SAMPLE_RATE * args.buffer_ms / 1000) as usize * CHANNELS as usize;

    // Create ring buffer for speaker audio data (double the buffer for safety)
    let speaker_buffer = Arc::new(AudioRingBuffer::new(buffer_samples * 4));

    // Create output device ID holder for hot-swapping
    let current_output_id = Arc::new(RwLock::new(args.speaker_out.clone()));

    // Create mic state if mic proxy is configured
    let mic_state = if let (Some(mic_in), Some(mic_out)) = (&args.mic_in, &args.mic_out) {
        let mic_buffer = Arc::new(AudioRingBuffer::new(buffer_samples * 4));
        Some(MicState {
            buffer: mic_buffer,
            input_id: Arc::new(RwLock::new(mic_in.clone())),
            output_id: mic_out.clone(),
            enabled: Arc::new(AtomicBool::new(true)),
        })
    } else {
        None
    };

    // Start IPC server
    let ipc_running = running.clone();
    let ipc_output_id = current_output_id.clone();
    let ipc_mic_input_id = mic_state.as_ref().map(|s| s.input_id.clone());
    let ipc_mic_enabled = mic_state.as_ref().map(|s| s.enabled.clone());
    let ipc_handle = thread::spawn(move || {
        if let Err(e) = run_ipc_server(ipc_running, ipc_output_id, ipc_mic_input_id, ipc_mic_enabled) {
            error!("IPC server error: {}", e);
        }
    });

    // Start speaker capture thread
    let capture_running = running.clone();
    let capture_buffer = speaker_buffer.clone();
    let capture_input_id = args.speaker_in.clone();
    let capture_handle = thread::spawn(move || {
        // Initialize COM for this thread
        unsafe {
            if CoInitializeEx(None, COINIT_MULTITHREADED).is_err() {
                error!("Failed to initialize COM in speaker capture thread");
                return;
            }
        }

        if let Err(e) = run_speaker_capture_loop(&capture_input_id, capture_buffer, capture_running) {
            error!("Speaker capture loop error: {}", e);
        }

        unsafe {
            CoUninitialize();
        }
    });

    // Start speaker render thread
    let render_running = running.clone();
    let render_buffer = speaker_buffer.clone();
    let render_output_id = current_output_id.clone();
    let buffer_ms = args.buffer_ms;
    let render_handle = thread::spawn(move || {
        // Initialize COM for this thread
        unsafe {
            if CoInitializeEx(None, COINIT_MULTITHREADED).is_err() {
                error!("Failed to initialize COM in speaker render thread");
                return;
            }
        }

        if let Err(e) = run_speaker_render_loop(render_buffer, render_output_id, render_running, buffer_ms) {
            error!("Speaker render loop error: {}", e);
        }

        unsafe {
            CoUninitialize();
        }
    });

    // Start mic threads if configured
    let mic_handles = if let Some(ref mic) = mic_state {
        // Mic capture thread
        let mic_capture_running = running.clone();
        let mic_capture_buffer = mic.buffer.clone();
        let mic_capture_input_id = mic.input_id.clone();
        let mic_capture_enabled = mic.enabled.clone();
        let mic_capture_handle = thread::spawn(move || {
            unsafe {
                if CoInitializeEx(None, COINIT_MULTITHREADED).is_err() {
                    error!("Failed to initialize COM in mic capture thread");
                    return;
                }
            }

            if let Err(e) = run_mic_capture_loop(
                mic_capture_input_id,
                mic_capture_buffer,
                mic_capture_running,
                mic_capture_enabled,
            ) {
                error!("Mic capture loop error: {}", e);
            }

            unsafe {
                CoUninitialize();
            }
        });

        // Mic render thread
        let mic_render_running = running.clone();
        let mic_render_buffer = mic.buffer.clone();
        let mic_render_output_id = mic.output_id.clone();
        let mic_render_enabled = mic.enabled.clone();
        let mic_render_handle = thread::spawn(move || {
            unsafe {
                if CoInitializeEx(None, COINIT_MULTITHREADED).is_err() {
                    error!("Failed to initialize COM in mic render thread");
                    return;
                }
            }

            if let Err(e) = run_mic_render_loop(
                &mic_render_output_id,
                mic_render_buffer,
                mic_render_running,
                mic_render_enabled,
                buffer_ms,
            ) {
                error!("Mic render loop error: {}", e);
            }

            unsafe {
                CoUninitialize();
            }
        });

        Some((mic_capture_handle, mic_render_handle))
    } else {
        None
    };

    // Wait for shutdown signal
    while running_clone.load(Ordering::SeqCst) {
        thread::sleep(Duration::from_millis(100));
    }

    info!("Shutting down...");

    // Wait for threads to finish
    let _ = capture_handle.join();
    let _ = render_handle.join();
    if let Some((mic_capture, mic_render)) = mic_handles {
        let _ = mic_capture.join();
        let _ = mic_render.join();
    }
    let _ = ipc_handle.join();

    info!("Audio Proxy stopped.");
    Ok(())
}

fn run_speaker_capture_loop(
    input_device_id: &str,
    buffer: Arc<AudioRingBuffer>,
    running: Arc<AtomicBool>,
) -> Result<()> {
    info!("Starting speaker capture from device: {}", input_device_id);

    let mut capture = CaptureStream::new(input_device_id, SAMPLE_RATE, CHANNELS)
        .context("Failed to create capture stream")?;

    capture.start().context("Failed to start capture")?;

    let mut temp_buffer = vec![0.0f32; 4096];

    while running.load(Ordering::SeqCst) {
        match capture.read(&mut temp_buffer) {
            Ok(samples_read) if samples_read > 0 => {
                let written = buffer.write(&temp_buffer[..samples_read]);
                if written < samples_read {
                    warn!("Speaker ring buffer overflow: {} samples dropped", samples_read - written);
                }
            }
            Ok(_) => {
                // No data available, wait a bit
                thread::sleep(Duration::from_micros(500));
            }
            Err(e) => {
                error!("Speaker capture error: {}", e);
                thread::sleep(Duration::from_millis(10));
            }
        }
    }

    capture.stop()?;
    info!("Speaker capture loop stopped.");
    Ok(())
}

fn run_speaker_render_loop(
    buffer: Arc<AudioRingBuffer>,
    output_device_id: Arc<RwLock<String>>,
    running: Arc<AtomicBool>,
    buffer_ms: u32,
) -> Result<()> {
    let device_id = output_device_id.read().unwrap().clone();
    info!("Starting speaker render to device: {}", device_id);

    let mut render = RenderStream::new(&device_id, SAMPLE_RATE, CHANNELS)
        .context("Failed to create render stream")?;

    render.start().context("Failed to start render")?;

    let mut current_device_id = device_id;
    let mut temp_buffer = vec![0.0f32; 4096];

    // Pre-fill buffer to reduce initial latency issues
    let prefill_samples = (SAMPLE_RATE * buffer_ms / 1000) as usize * CHANNELS as usize;
    let silence = vec![0.0f32; prefill_samples];
    let _ = render.write(&silence);

    while running.load(Ordering::SeqCst) {
        // Check if output device changed
        {
            let new_device_id = output_device_id.read().unwrap().clone();
            if new_device_id != current_device_id {
                info!("Switching speaker output to: {}", new_device_id);
                render.stop()?;

                render = RenderStream::new(&new_device_id, SAMPLE_RATE, CHANNELS)
                    .context("Failed to create new render stream")?;
                render.start().context("Failed to start new render stream")?;

                current_device_id = new_device_id;
                info!("Speaker output switched successfully");
            }
        }

        // Read from ring buffer and write to output
        let samples_read = buffer.read(&mut temp_buffer);
        if samples_read > 0 {
            if let Err(e) = render.write(&temp_buffer[..samples_read]) {
                error!("Speaker render error: {}", e);
                thread::sleep(Duration::from_millis(1));
            }
        } else {
            // No data available - write silence to prevent underrun
            let silence_samples = (SAMPLE_RATE / 1000) as usize * CHANNELS as usize; // 1ms of silence
            let silence = vec![0.0f32; silence_samples];
            let _ = render.write(&silence);
            thread::sleep(Duration::from_micros(500));
        }
    }

    render.stop()?;
    info!("Speaker render loop stopped.");
    Ok(())
}

/// Microphone capture loop - captures from physical mic with hot-swap support
fn run_mic_capture_loop(
    mic_input_id: Arc<RwLock<String>>,
    buffer: Arc<AudioRingBuffer>,
    running: Arc<AtomicBool>,
    mic_enabled: Arc<AtomicBool>,
) -> Result<()> {
    let device_id = mic_input_id.read().unwrap().clone();
    info!("Starting mic capture from device: {}", device_id);

    let mut capture = CaptureStream::new(&device_id, SAMPLE_RATE, CHANNELS)
        .context("Failed to create mic capture stream")?;

    capture.start().context("Failed to start mic capture")?;

    let mut current_device_id = device_id;
    let mut temp_buffer = vec![0.0f32; 4096];

    while running.load(Ordering::SeqCst) {
        // Check if mic is disabled
        if !mic_enabled.load(Ordering::SeqCst) {
            thread::sleep(Duration::from_millis(50));
            continue;
        }

        // Check if input device changed (hot-swap)
        {
            let new_device_id = mic_input_id.read().unwrap().clone();
            if new_device_id != current_device_id {
                info!("Switching mic input to: {}", new_device_id);
                capture.stop()?;

                capture = CaptureStream::new(&new_device_id, SAMPLE_RATE, CHANNELS)
                    .context("Failed to create new mic capture stream")?;
                capture.start().context("Failed to start new mic capture stream")?;

                current_device_id = new_device_id;
                info!("Mic input switched successfully");
            }
        }

        match capture.read(&mut temp_buffer) {
            Ok(samples_read) if samples_read > 0 => {
                let written = buffer.write(&temp_buffer[..samples_read]);
                if written < samples_read {
                    warn!("Mic ring buffer overflow: {} samples dropped", samples_read - written);
                }
            }
            Ok(_) => {
                // No data available, wait a bit
                thread::sleep(Duration::from_micros(500));
            }
            Err(e) => {
                error!("Mic capture error: {}", e);
                thread::sleep(Duration::from_millis(10));
            }
        }
    }

    capture.stop()?;
    info!("Mic capture loop stopped.");
    Ok(())
}

/// Microphone render loop - renders to VB-Cable Input (fixed device)
fn run_mic_render_loop(
    mic_output_id: &str,
    buffer: Arc<AudioRingBuffer>,
    running: Arc<AtomicBool>,
    mic_enabled: Arc<AtomicBool>,
    buffer_ms: u32,
) -> Result<()> {
    info!("Starting mic render to device: {}", mic_output_id);

    let mut render = RenderStream::new(mic_output_id, SAMPLE_RATE, CHANNELS)
        .context("Failed to create mic render stream")?;

    render.start().context("Failed to start mic render")?;

    let mut temp_buffer = vec![0.0f32; 4096];

    // Pre-fill buffer to reduce initial latency issues
    let prefill_samples = (SAMPLE_RATE * buffer_ms / 1000) as usize * CHANNELS as usize;
    let silence = vec![0.0f32; prefill_samples];
    let _ = render.write(&silence);

    while running.load(Ordering::SeqCst) {
        // Check if mic is disabled
        if !mic_enabled.load(Ordering::SeqCst) {
            // Write silence when disabled to prevent audio glitches
            let silence_samples = (SAMPLE_RATE / 1000) as usize * CHANNELS as usize;
            let silence = vec![0.0f32; silence_samples];
            let _ = render.write(&silence);
            thread::sleep(Duration::from_millis(10));
            continue;
        }

        // Read from ring buffer and write to output
        let samples_read = buffer.read(&mut temp_buffer);
        if samples_read > 0 {
            if let Err(e) = render.write(&temp_buffer[..samples_read]) {
                error!("Mic render error: {}", e);
                thread::sleep(Duration::from_millis(1));
            }
        } else {
            // No data available - write silence to prevent underrun
            let silence_samples = (SAMPLE_RATE / 1000) as usize * CHANNELS as usize;
            let silence = vec![0.0f32; silence_samples];
            let _ = render.write(&silence);
            thread::sleep(Duration::from_micros(500));
        }
    }

    render.stop()?;
    info!("Mic render loop stopped.");
    Ok(())
}

fn run_ipc_server(
    running: Arc<AtomicBool>,
    output_device_id: Arc<RwLock<String>>,
    mic_input_id: Option<Arc<RwLock<String>>>,
    mic_enabled: Option<Arc<AtomicBool>>,
) -> Result<()> {
    let mut server = IpcServer::new()?;
    info!("IPC server started on pipe: {}", ipc::PIPE_NAME);

    while running.load(Ordering::SeqCst) {
        match server.accept_with_timeout(Duration::from_millis(100)) {
            Ok(Some(command)) => {
                let response = handle_ipc_command(
                    command,
                    &output_device_id,
                    &running,
                    mic_input_id.as_ref(),
                    mic_enabled.as_ref(),
                );
                if let Err(e) = server.send_response(&response) {
                    warn!("Failed to send IPC response: {}", e);
                }
            }
            Ok(None) => {
                // Timeout, continue loop
            }
            Err(e) => {
                warn!("IPC accept error: {}", e);
                thread::sleep(Duration::from_millis(100));
            }
        }
    }

    Ok(())
}

fn handle_ipc_command(
    command: IpcCommand,
    output_device_id: &Arc<RwLock<String>>,
    running: &Arc<AtomicBool>,
    mic_input_id: Option<&Arc<RwLock<String>>>,
    mic_enabled: Option<&Arc<AtomicBool>>,
) -> ipc::IpcResponse {
    match command {
        IpcCommand::SetOutput { device_id } => {
            info!("IPC: Setting speaker output device to: {}", device_id);
            *output_device_id.write().unwrap() = device_id;
            ipc::IpcResponse::success("Output device updated")
        }
        IpcCommand::GetStatus => {
            let current_output = output_device_id.read().unwrap().clone();
            let is_running = running.load(Ordering::SeqCst);

            if let (Some(mic_id), Some(mic_en)) = (mic_input_id, mic_enabled) {
                let mic_input = mic_id.read().unwrap().clone();
                let mic_is_enabled = mic_en.load(Ordering::SeqCst);
                ipc::IpcResponse::status_full(is_running, &current_output, mic_is_enabled, Some(&mic_input))
            } else {
                ipc::IpcResponse::status(is_running, &current_output)
            }
        }
        IpcCommand::Stop => {
            info!("IPC: Stop command received");
            running.store(false, Ordering::SeqCst);
            ipc::IpcResponse::success("Stopping proxy")
        }
        IpcCommand::SetMicInput { device_id } => {
            if let Some(mic_id) = mic_input_id {
                info!("IPC: Setting mic input device to: {}", device_id);
                *mic_id.write().unwrap() = device_id;
                ipc::IpcResponse::success("Mic input device updated")
            } else {
                ipc::IpcResponse::error("Mic proxy not configured")
            }
        }
        IpcCommand::EnableMic { enabled } => {
            if let Some(mic_en) = mic_enabled {
                info!("IPC: Setting mic enabled to: {}", enabled);
                mic_en.store(enabled, Ordering::SeqCst);
                ipc::IpcResponse::success(if enabled { "Mic proxy enabled" } else { "Mic proxy disabled" })
            } else {
                ipc::IpcResponse::error("Mic proxy not configured")
            }
        }
    }
}

fn ctrlc_handler(running: Arc<AtomicBool>) {
    let _ = ctrlc::set_handler(move || {
        info!("Ctrl+C received, shutting down...");
        running.store(false, Ordering::SeqCst);
    });
}
