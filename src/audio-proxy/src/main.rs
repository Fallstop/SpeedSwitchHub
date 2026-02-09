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

use audio_stream::{AudioFormat, CaptureStream, RenderStream};
use ipc::{IpcCommand, IpcServer};
use ring_buffer::AudioRingBuffer;

/// Default buffer size in milliseconds
const DEFAULT_BUFFER_MS: u32 = 10;

/// Default sample rate for buffer size estimation (actual rate comes from device)
const DEFAULT_SAMPLE_RATE: u32 = 48000;

/// Default channel count for buffer size estimation
const DEFAULT_CHANNELS: u16 = 2;

/// Max consecutive errors before giving up on stream recovery
const MAX_RECOVERY_ATTEMPTS: u32 = 5;

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
    buffer: Arc<AudioRingBuffer>,
    input_id: Arc<RwLock<String>>,
    output_id: String,
    enabled: Arc<AtomicBool>,
    capture_format: Arc<RwLock<Option<AudioFormat>>>,
}

fn run_proxy(args: &Args) -> Result<()> {
    let running = Arc::new(AtomicBool::new(true));
    let running_clone = running.clone();

    // Set up Ctrl+C handler
    ctrlc_handler(running.clone());

    // Calculate buffer size in samples (estimate - actual format comes from device)
    let buffer_samples = (DEFAULT_SAMPLE_RATE * args.buffer_ms / 1000) as usize * DEFAULT_CHANNELS as usize;

    // Create ring buffer for speaker audio data
    let speaker_buffer = Arc::new(AudioRingBuffer::new(buffer_samples * 4));

    // Create output device ID holder for hot-swapping
    let current_output_id = Arc::new(RwLock::new(args.speaker_out.clone()));

    // Shared capture format so render thread can do conversion if needed
    let speaker_capture_format: Arc<RwLock<Option<AudioFormat>>> = Arc::new(RwLock::new(None));

    // Create mic state if mic proxy is configured
    let mic_state = if let (Some(mic_in), Some(mic_out)) = (&args.mic_in, &args.mic_out) {
        let mic_buffer = Arc::new(AudioRingBuffer::new(buffer_samples * 4));
        Some(MicState {
            buffer: mic_buffer,
            input_id: Arc::new(RwLock::new(mic_in.clone())),
            output_id: mic_out.clone(),
            enabled: Arc::new(AtomicBool::new(true)),
            capture_format: Arc::new(RwLock::new(None)),
        })
    } else {
        None
    };

    // Start IPC server
    let ipc_running = running.clone();
    let ipc_output_id = current_output_id.clone();
    let ipc_mic_input_id = mic_state.as_ref().map(|s| s.input_id.clone());
    let ipc_mic_enabled = mic_state.as_ref().map(|s| s.enabled.clone());
    let _ipc_handle = thread::spawn(move || {
        if let Err(e) = run_ipc_server(ipc_running, ipc_output_id, ipc_mic_input_id, ipc_mic_enabled) {
            error!("IPC server error: {}", e);
        }
    });

    // Start speaker capture thread
    let capture_running = running.clone();
    let capture_buffer = speaker_buffer.clone();
    let capture_input_id = args.speaker_in.clone();
    let capture_format_shared = speaker_capture_format.clone();
    let capture_handle = thread::spawn(move || {
        unsafe {
            if CoInitializeEx(None, COINIT_MULTITHREADED).is_err() {
                error!("Failed to initialize COM in speaker capture thread");
                return;
            }
        }

        if let Err(e) = run_speaker_capture_loop(
            &capture_input_id, capture_buffer, capture_running, capture_format_shared,
        ) {
            error!("Speaker capture loop error: {}", e);
        }

        unsafe { CoUninitialize(); }
    });

    // Start speaker render thread
    let render_running = running.clone();
    let render_buffer = speaker_buffer.clone();
    let render_output_id = current_output_id.clone();
    let render_capture_format = speaker_capture_format.clone();
    let buffer_ms = args.buffer_ms;
    let render_handle = thread::spawn(move || {
        unsafe {
            if CoInitializeEx(None, COINIT_MULTITHREADED).is_err() {
                error!("Failed to initialize COM in speaker render thread");
                return;
            }
        }

        if let Err(e) = run_speaker_render_loop(
            render_buffer, render_output_id, render_running, buffer_ms, render_capture_format,
        ) {
            error!("Speaker render loop error: {}", e);
        }

        unsafe { CoUninitialize(); }
    });

    // Start mic threads if configured
    let mic_handles = if let Some(ref mic) = mic_state {
        let mic_capture_running = running.clone();
        let mic_capture_buffer = mic.buffer.clone();
        let mic_capture_input_id = mic.input_id.clone();
        let mic_capture_enabled = mic.enabled.clone();
        let mic_capture_format = mic.capture_format.clone();
        let mic_capture_handle = thread::spawn(move || {
            unsafe {
                if CoInitializeEx(None, COINIT_MULTITHREADED).is_err() {
                    error!("Failed to initialize COM in mic capture thread");
                    return;
                }
            }

            if let Err(e) = run_mic_capture_loop(
                mic_capture_input_id, mic_capture_buffer, mic_capture_running,
                mic_capture_enabled, mic_capture_format,
            ) {
                error!("Mic capture loop error: {}", e);
            }

            unsafe { CoUninitialize(); }
        });

        let mic_render_running = running.clone();
        let mic_render_buffer = mic.buffer.clone();
        let mic_render_output_id = mic.output_id.clone();
        let mic_render_enabled = mic.enabled.clone();
        let mic_render_capture_format = mic.capture_format.clone();
        let mic_render_handle = thread::spawn(move || {
            unsafe {
                if CoInitializeEx(None, COINIT_MULTITHREADED).is_err() {
                    error!("Failed to initialize COM in mic render thread");
                    return;
                }
            }

            if let Err(e) = run_mic_render_loop(
                &mic_render_output_id, mic_render_buffer, mic_render_running,
                mic_render_enabled, buffer_ms, mic_render_capture_format,
            ) {
                error!("Mic render loop error: {}", e);
            }

            unsafe { CoUninitialize(); }
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

    // Wait for audio threads to finish (they check the running flag)
    let _ = capture_handle.join();
    let _ = render_handle.join();
    if let Some((mic_capture, mic_render)) = mic_handles {
        let _ = mic_capture.join();
        let _ = mic_render.join();
    }
    // IPC thread is detached (_ipc_handle dropped) - it may be blocked in
    // ConnectNamedPipe, so we let it be cleaned up on process exit.

    info!("Audio Proxy stopped.");
    Ok(())
}

// ── Audio format conversion utilities ──────────────────────────────────────

/// Convert channel count: upmix, downmix, or passthrough
fn convert_channels(input: &[f32], in_ch: usize, out_ch: usize, output: &mut Vec<f32>) {
    let frames = input.len() / in_ch;
    output.clear();
    output.reserve(frames * out_ch);

    for frame in 0..frames {
        let in_start = frame * in_ch;
        if out_ch <= in_ch {
            // Downmix: take first out_ch channels (simple truncation)
            // For stereo->mono, average L+R
            if in_ch == 2 && out_ch == 1 {
                output.push((input[in_start] + input[in_start + 1]) * 0.5);
            } else {
                for ch in 0..out_ch {
                    output.push(input[in_start + ch]);
                }
            }
        } else {
            // Upmix: copy available channels, duplicate first for the rest
            for ch in 0..out_ch {
                if ch < in_ch {
                    output.push(input[in_start + ch]);
                } else {
                    output.push(input[in_start]); // duplicate first channel
                }
            }
        }
    }
}

/// Resample audio using linear interpolation
fn resample(input: &[f32], in_rate: u32, out_rate: u32, channels: usize, output: &mut Vec<f32>) {
    let in_frames = input.len() / channels;
    if in_frames == 0 {
        output.clear();
        return;
    }

    let ratio = out_rate as f64 / in_rate as f64;
    let out_frames = (in_frames as f64 * ratio).ceil() as usize;
    output.clear();
    output.reserve(out_frames * channels);

    for frame in 0..out_frames {
        let src_pos = frame as f64 / ratio;
        let src_idx = src_pos as usize;
        let frac = (src_pos - src_idx as f64) as f32;

        let idx0 = src_idx.min(in_frames - 1);
        let idx1 = (src_idx + 1).min(in_frames - 1);

        for ch in 0..channels {
            let s0 = input[idx0 * channels + ch];
            let s1 = input[idx1 * channels + ch];
            output.push(s0 + frac * (s1 - s0));
        }
    }
}

/// Check if two formats need conversion
fn formats_need_conversion(cap: &AudioFormat, rnd: &AudioFormat) -> bool {
    cap.sample_rate != rnd.sample_rate || cap.channels != rnd.channels
}

/// Convert audio from capture format to render format.
/// Uses pre-allocated scratch buffer to avoid repeated allocations.
fn convert_audio(
    input: &[f32],
    cap_fmt: &AudioFormat,
    rnd_fmt: &AudioFormat,
    scratch: &mut Vec<f32>,
) -> Vec<f32> {
    let mut current = input;
    let mut temp = Vec::new();

    // Channel conversion first (if needed)
    if cap_fmt.channels != rnd_fmt.channels {
        convert_channels(current, cap_fmt.channels as usize, rnd_fmt.channels as usize, scratch);
        std::mem::swap(scratch, &mut temp);
        current = &temp;
    }

    // Then resample (if needed)
    if cap_fmt.sample_rate != rnd_fmt.sample_rate {
        resample(current, cap_fmt.sample_rate, rnd_fmt.sample_rate, rnd_fmt.channels as usize, scratch);
        return std::mem::take(scratch);
    }

    current.to_vec()
}

// ── Stream creation with error recovery ────────────────────────────────────

fn create_and_start_capture(device_id: &str) -> Result<CaptureStream> {
    let mut capture = CaptureStream::new(device_id)
        .context("Failed to create capture stream")?;
    capture.start().context("Failed to start capture")?;
    Ok(capture)
}

fn create_and_start_render(device_id: &str) -> Result<RenderStream> {
    let mut render = RenderStream::new(device_id)
        .context("Failed to create render stream")?;
    render.start().context("Failed to start render")?;
    Ok(render)
}

// ── Speaker loops ──────────────────────────────────────────────────────────

fn run_speaker_capture_loop(
    input_device_id: &str,
    buffer: Arc<AudioRingBuffer>,
    running: Arc<AtomicBool>,
    capture_format: Arc<RwLock<Option<AudioFormat>>>,
) -> Result<()> {
    info!("Starting speaker capture from device: {}", input_device_id);

    let mut capture = create_and_start_capture(input_device_id)?;

    // Share the format with the render thread
    if let Some(fmt) = capture.format() {
        *capture_format.write().unwrap() = Some(fmt.clone());
    }

    let mut temp_buffer = vec![0.0f32; 4096];
    let mut error_count: u32 = 0;

    while running.load(Ordering::SeqCst) {
        match capture.read(&mut temp_buffer) {
            Ok(samples_read) if samples_read > 0 => {
                error_count = 0;
                let written = buffer.write(&temp_buffer[..samples_read]);
                if written < samples_read {
                    warn!("Speaker ring buffer overflow: {} samples dropped", samples_read - written);
                }
            }
            Ok(_) => {
                thread::sleep(Duration::from_micros(500));
            }
            Err(e) => {
                error_count += 1;
                error!("Speaker capture error (attempt {}): {}", error_count, e);

                if error_count >= MAX_RECOVERY_ATTEMPTS {
                    return Err(e.context("Too many consecutive capture errors, giving up"));
                }

                warn!("Attempting to recover speaker capture stream...");
                thread::sleep(Duration::from_secs(1));
                match create_and_start_capture(input_device_id) {
                    Ok(new_capture) => {
                        capture = new_capture;
                        if let Some(fmt) = capture.format() {
                            *capture_format.write().unwrap() = Some(fmt.clone());
                        }
                        info!("Speaker capture stream recovered");
                    }
                    Err(e) => {
                        error!("Failed to recover speaker capture: {}", e);
                    }
                }
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
    capture_format: Arc<RwLock<Option<AudioFormat>>>,
) -> Result<()> {
    let device_id = output_device_id.read().unwrap().clone();
    info!("Starting speaker render to device: {}", device_id);

    let mut render = create_and_start_render(&device_id)?;
    let mut current_device_id = device_id;
    let mut temp_buffer = vec![0.0f32; 4096];
    let mut conversion_scratch = Vec::new();
    let mut error_count: u32 = 0;

    // Pre-fill buffer with silence
    let render_channels = render.format().map(|f| f.channels as usize).unwrap_or(2);
    let render_rate = render.format().map(|f| f.sample_rate).unwrap_or(DEFAULT_SAMPLE_RATE);
    let prefill_samples = (render_rate * buffer_ms / 1000) as usize * render_channels;
    let silence = vec![0.0f32; prefill_samples];
    let _ = render.write(&silence);

    while running.load(Ordering::SeqCst) {
        // Check if output device changed (hot-swap)
        {
            let new_device_id = output_device_id.read().unwrap().clone();
            if new_device_id != current_device_id {
                info!("Switching speaker output to: {}", new_device_id);
                render.stop()?;

                match create_and_start_render(&new_device_id) {
                    Ok(new_render) => {
                        render = new_render;
                        current_device_id = new_device_id;
                        error_count = 0;
                        info!("Speaker output switched successfully");
                    }
                    Err(e) => {
                        error!("Failed to switch speaker output: {}", e);
                        // Try to restart with old device
                        render = create_and_start_render(&current_device_id)
                            .context("Failed to restart render with previous device")?;
                    }
                }
            }
        }

        // Read from ring buffer and write to output
        let samples_read = buffer.read(&mut temp_buffer);
        if samples_read > 0 {
            // Check if format conversion is needed
            let cap_fmt = capture_format.read().unwrap().clone();
            let rnd_fmt = render.format().cloned();

            let write_result = if let (Some(ref cf), Some(ref rf)) = (cap_fmt, rnd_fmt) {
                if formats_need_conversion(cf, rf) {
                    let converted = convert_audio(
                        &temp_buffer[..samples_read], cf, rf, &mut conversion_scratch,
                    );
                    render.write(&converted)
                } else {
                    render.write(&temp_buffer[..samples_read])
                }
            } else {
                render.write(&temp_buffer[..samples_read])
            };

            if let Err(e) = write_result {
                error_count += 1;
                error!("Speaker render error (attempt {}): {}", error_count, e);

                if error_count >= MAX_RECOVERY_ATTEMPTS {
                    return Err(e.context("Too many consecutive render errors, giving up"));
                }

                warn!("Attempting to recover speaker render stream...");
                thread::sleep(Duration::from_secs(1));
                match create_and_start_render(&current_device_id) {
                    Ok(new_render) => {
                        render = new_render;
                        info!("Speaker render stream recovered");
                    }
                    Err(re) => {
                        error!("Failed to recover speaker render: {}", re);
                    }
                }
            } else {
                error_count = 0;
            }
        } else {
            // No data available - write silence to prevent underrun
            let ch = render.format().map(|f| f.channels as usize).unwrap_or(2);
            let rate = render.format().map(|f| f.sample_rate).unwrap_or(DEFAULT_SAMPLE_RATE);
            let silence_samples = (rate / 1000) as usize * ch; // 1ms of silence
            let silence = vec![0.0f32; silence_samples];
            let _ = render.write(&silence);
            thread::sleep(Duration::from_micros(500));
        }
    }

    render.stop()?;
    info!("Speaker render loop stopped.");
    Ok(())
}

// ── Microphone loops ───────────────────────────────────────────────────────

fn run_mic_capture_loop(
    mic_input_id: Arc<RwLock<String>>,
    buffer: Arc<AudioRingBuffer>,
    running: Arc<AtomicBool>,
    mic_enabled: Arc<AtomicBool>,
    capture_format: Arc<RwLock<Option<AudioFormat>>>,
) -> Result<()> {
    let device_id = mic_input_id.read().unwrap().clone();
    info!("Starting mic capture from device: {}", device_id);

    let mut capture = create_and_start_capture(&device_id)?;

    if let Some(fmt) = capture.format() {
        *capture_format.write().unwrap() = Some(fmt.clone());
    }

    let mut current_device_id = device_id;
    let mut temp_buffer = vec![0.0f32; 4096];
    let mut error_count: u32 = 0;

    while running.load(Ordering::SeqCst) {
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

                match create_and_start_capture(&new_device_id) {
                    Ok(new_capture) => {
                        capture = new_capture;
                        if let Some(fmt) = capture.format() {
                            *capture_format.write().unwrap() = Some(fmt.clone());
                        }
                        current_device_id = new_device_id;
                        error_count = 0;
                        info!("Mic input switched successfully");
                    }
                    Err(e) => {
                        error!("Failed to switch mic input: {}", e);
                        capture = create_and_start_capture(&current_device_id)
                            .context("Failed to restart mic capture with previous device")?;
                    }
                }
            }
        }

        match capture.read(&mut temp_buffer) {
            Ok(samples_read) if samples_read > 0 => {
                error_count = 0;
                let written = buffer.write(&temp_buffer[..samples_read]);
                if written < samples_read {
                    warn!("Mic ring buffer overflow: {} samples dropped", samples_read - written);
                }
            }
            Ok(_) => {
                thread::sleep(Duration::from_micros(500));
            }
            Err(e) => {
                error_count += 1;
                error!("Mic capture error (attempt {}): {}", error_count, e);

                if error_count >= MAX_RECOVERY_ATTEMPTS {
                    return Err(e.context("Too many consecutive mic capture errors, giving up"));
                }

                warn!("Attempting to recover mic capture stream...");
                thread::sleep(Duration::from_secs(1));
                match create_and_start_capture(&current_device_id) {
                    Ok(new_capture) => {
                        capture = new_capture;
                        if let Some(fmt) = capture.format() {
                            *capture_format.write().unwrap() = Some(fmt.clone());
                        }
                        info!("Mic capture stream recovered");
                    }
                    Err(re) => {
                        error!("Failed to recover mic capture: {}", re);
                    }
                }
            }
        }
    }

    capture.stop()?;
    info!("Mic capture loop stopped.");
    Ok(())
}

fn run_mic_render_loop(
    mic_output_id: &str,
    buffer: Arc<AudioRingBuffer>,
    running: Arc<AtomicBool>,
    mic_enabled: Arc<AtomicBool>,
    buffer_ms: u32,
    capture_format: Arc<RwLock<Option<AudioFormat>>>,
) -> Result<()> {
    info!("Starting mic render to device: {}", mic_output_id);

    let mut render = create_and_start_render(mic_output_id)?;
    let mut temp_buffer = vec![0.0f32; 4096];
    let mut conversion_scratch = Vec::new();
    let mut error_count: u32 = 0;

    let render_channels = render.format().map(|f| f.channels as usize).unwrap_or(2);
    let render_rate = render.format().map(|f| f.sample_rate).unwrap_or(DEFAULT_SAMPLE_RATE);
    let prefill_samples = (render_rate * buffer_ms / 1000) as usize * render_channels;
    let silence = vec![0.0f32; prefill_samples];
    let _ = render.write(&silence);

    while running.load(Ordering::SeqCst) {
        if !mic_enabled.load(Ordering::SeqCst) {
            let ch = render.format().map(|f| f.channels as usize).unwrap_or(2);
            let rate = render.format().map(|f| f.sample_rate).unwrap_or(DEFAULT_SAMPLE_RATE);
            let silence_samples = (rate / 1000) as usize * ch;
            let silence = vec![0.0f32; silence_samples];
            let _ = render.write(&silence);
            thread::sleep(Duration::from_millis(10));
            continue;
        }

        let samples_read = buffer.read(&mut temp_buffer);
        if samples_read > 0 {
            let cap_fmt = capture_format.read().unwrap().clone();
            let rnd_fmt = render.format().cloned();

            let write_result = if let (Some(ref cf), Some(ref rf)) = (cap_fmt, rnd_fmt) {
                if formats_need_conversion(cf, rf) {
                    let converted = convert_audio(
                        &temp_buffer[..samples_read], cf, rf, &mut conversion_scratch,
                    );
                    render.write(&converted)
                } else {
                    render.write(&temp_buffer[..samples_read])
                }
            } else {
                render.write(&temp_buffer[..samples_read])
            };

            if let Err(e) = write_result {
                error_count += 1;
                error!("Mic render error (attempt {}): {}", error_count, e);

                if error_count >= MAX_RECOVERY_ATTEMPTS {
                    return Err(e.context("Too many consecutive mic render errors, giving up"));
                }

                warn!("Attempting to recover mic render stream...");
                thread::sleep(Duration::from_secs(1));
                match create_and_start_render(mic_output_id) {
                    Ok(new_render) => {
                        render = new_render;
                        info!("Mic render stream recovered");
                    }
                    Err(re) => {
                        error!("Failed to recover mic render: {}", re);
                    }
                }
            } else {
                error_count = 0;
            }
        } else {
            let ch = render.format().map(|f| f.channels as usize).unwrap_or(2);
            let rate = render.format().map(|f| f.sample_rate).unwrap_or(DEFAULT_SAMPLE_RATE);
            let silence_samples = (rate / 1000) as usize * ch;
            let silence = vec![0.0f32; silence_samples];
            let _ = render.write(&silence);
            thread::sleep(Duration::from_micros(500));
        }
    }

    render.stop()?;
    info!("Mic render loop stopped.");
    Ok(())
}

// ── IPC server ─────────────────────────────────────────────────────────────

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
                // Timeout or no client, continue loop
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
