//! WASAPI audio stream management for capture and render

use anyhow::{anyhow, Context, Result};
use log::{debug, info, warn};
use wasapi::{DeviceCollection, Direction, ShareMode};

/// Audio format information from the device
#[derive(Debug, Clone)]
pub struct AudioFormat {
    pub sample_rate: u32,
    pub channels: u16,
    pub bits_per_sample: u16,
    pub block_align: u32, // bytes per frame
}

/// Audio capture stream from a device (e.g., VB-Cable)
pub struct CaptureStream {
    device: wasapi::Device,
    client: Option<wasapi::AudioClient>,
    capture_client: Option<wasapi::AudioCaptureClient>,
    format: Option<AudioFormat>,
    started: bool,
}

impl CaptureStream {
    /// Create a new capture stream for the specified device
    pub fn new(device_id: &str) -> Result<Self> {
        info!("Creating capture stream for device: {}", device_id);

        let device = find_device_by_id(device_id, Direction::Capture)
            .context("Failed to find capture device")?;

        Ok(Self {
            device,
            client: None,
            capture_client: None,
            format: None,
            started: false,
        })
    }

    /// Start capturing audio
    pub fn start(&mut self) -> Result<()> {
        if self.started {
            return Ok(());
        }

        let mut client = self.device.get_iaudioclient()
            .map_err(|e| anyhow!("Failed to get audio client: {}", e))?;

        let wave_format = client.get_mixformat()
            .map_err(|e| anyhow!("Failed to get mix format: {}", e))?;

        let format = AudioFormat {
            sample_rate: wave_format.get_samplespersec(),
            channels: wave_format.get_nchannels(),
            bits_per_sample: wave_format.get_bitspersample(),
            block_align: wave_format.get_blockalign(),
        };

        info!("Capture format: {} Hz, {} ch, {}-bit, {} bytes/frame",
              format.sample_rate, format.channels, format.bits_per_sample, format.block_align);

        if format.bits_per_sample != 32 {
            return Err(anyhow!(
                "Unsupported capture format: {}-bit (only 32-bit float supported in shared mode)",
                format.bits_per_sample
            ));
        }

        client.initialize_client(
            &wave_format,
            100_000, // 10ms buffer in 100ns units
            &Direction::Capture,
            &ShareMode::Shared,
            false,
        ).map_err(|e| anyhow!("Failed to initialize capture client: {}", e))?;

        let capture_client = client.get_audiocaptureclient()
            .map_err(|e| anyhow!("Failed to get capture client: {}", e))?;

        client.start_stream()
            .map_err(|e| anyhow!("Failed to start capture stream: {}", e))?;

        self.client = Some(client);
        self.capture_client = Some(capture_client);
        self.format = Some(format);
        self.started = true;
        info!("Capture stream started");
        Ok(())
    }

    /// Stop capturing audio
    pub fn stop(&mut self) -> Result<()> {
        if !self.started {
            return Ok(());
        }

        if let Some(ref mut client) = self.client {
            client.stop_stream()
                .map_err(|e| anyhow!("Failed to stop capture stream: {}", e))?;
        }

        self.started = false;
        info!("Capture stream stopped");
        Ok(())
    }

    /// Get the audio format (available after start)
    pub fn format(&self) -> Option<&AudioFormat> {
        self.format.as_ref()
    }

    /// Read audio samples from the capture buffer
    /// Returns the number of f32 samples read (samples = frames * channels)
    pub fn read(&mut self, buffer: &mut [f32]) -> Result<usize> {
        let capture_client = self.capture_client.as_mut()
            .ok_or_else(|| anyhow!("Capture client not initialized"))?;
        let format = self.format.as_ref()
            .ok_or_else(|| anyhow!("Format not initialized"))?;

        let available_frames = match capture_client.get_next_nbr_frames()
            .map_err(|e| anyhow!("Failed to get frame count: {}", e))? {
            Some(frames) => frames as usize,
            None => return Ok(0),
        };

        if available_frames == 0 {
            return Ok(0);
        }

        let bytes_per_frame = format.block_align as usize;
        let mut byte_buffer = vec![0u8; available_frames * bytes_per_frame];
        let (frames_read, _flags) = capture_client.read_from_device(&mut byte_buffer)
            .map_err(|e| anyhow!("Failed to read from device: {}", e))?;

        let actual_bytes = frames_read as usize * bytes_per_frame;
        let samples_read = bytes_to_f32(&byte_buffer[..actual_bytes], buffer);

        debug!("Captured {} samples ({} frames)", samples_read, frames_read);
        Ok(samples_read)
    }
}

impl Drop for CaptureStream {
    fn drop(&mut self) {
        let _ = self.stop();
    }
}

/// Audio render stream to a device
pub struct RenderStream {
    device: wasapi::Device,
    client: Option<wasapi::AudioClient>,
    render_client: Option<wasapi::AudioRenderClient>,
    buffer_frame_count: u32,
    format: Option<AudioFormat>,
    started: bool,
}

impl RenderStream {
    /// Create a new render stream for the specified device
    pub fn new(device_id: &str) -> Result<Self> {
        info!("Creating render stream for device: {}", device_id);

        let device = find_device_by_id(device_id, Direction::Render)
            .context("Failed to find render device")?;

        Ok(Self {
            device,
            client: None,
            render_client: None,
            buffer_frame_count: 0,
            format: None,
            started: false,
        })
    }

    /// Start rendering audio
    pub fn start(&mut self) -> Result<()> {
        if self.started {
            return Ok(());
        }

        let mut client = self.device.get_iaudioclient()
            .map_err(|e| anyhow!("Failed to get audio client: {}", e))?;

        let wave_format = client.get_mixformat()
            .map_err(|e| anyhow!("Failed to get mix format: {}", e))?;

        let format = AudioFormat {
            sample_rate: wave_format.get_samplespersec(),
            channels: wave_format.get_nchannels(),
            bits_per_sample: wave_format.get_bitspersample(),
            block_align: wave_format.get_blockalign(),
        };

        info!("Render format: {} Hz, {} ch, {}-bit, {} bytes/frame",
              format.sample_rate, format.channels, format.bits_per_sample, format.block_align);

        if format.bits_per_sample != 32 {
            return Err(anyhow!(
                "Unsupported render format: {}-bit (only 32-bit float supported in shared mode)",
                format.bits_per_sample
            ));
        }

        client.initialize_client(
            &wave_format,
            100_000, // 10ms buffer in 100ns units
            &Direction::Render,
            &ShareMode::Shared,
            false,
        ).map_err(|e| anyhow!("Failed to initialize render client: {}", e))?;

        let buffer_frame_count = client.get_bufferframecount()
            .map_err(|e| anyhow!("Failed to get buffer frame count: {}", e))?;

        let render_client = client.get_audiorenderclient()
            .map_err(|e| anyhow!("Failed to get render client: {}", e))?;

        client.start_stream()
            .map_err(|e| anyhow!("Failed to start render stream: {}", e))?;

        self.client = Some(client);
        self.render_client = Some(render_client);
        self.buffer_frame_count = buffer_frame_count;
        self.format = Some(format);
        self.started = true;
        info!("Render stream started ({} frames buffer)", buffer_frame_count);
        Ok(())
    }

    /// Stop rendering audio
    pub fn stop(&mut self) -> Result<()> {
        if !self.started {
            return Ok(());
        }

        if let Some(ref mut client) = self.client {
            client.stop_stream()
                .map_err(|e| anyhow!("Failed to stop render stream: {}", e))?;
        }

        self.started = false;
        info!("Render stream stopped");
        Ok(())
    }

    /// Get the audio format (available after start)
    pub fn format(&self) -> Option<&AudioFormat> {
        self.format.as_ref()
    }

    /// Write audio samples to the render buffer
    /// Returns the number of samples written
    pub fn write(&mut self, samples: &[f32]) -> Result<usize> {
        let client = self.client.as_ref()
            .ok_or_else(|| anyhow!("Client not initialized"))?;
        let render_client = self.render_client.as_mut()
            .ok_or_else(|| anyhow!("Render client not initialized"))?;
        let format = self.format.as_ref()
            .ok_or_else(|| anyhow!("Format not initialized"))?;

        let padding = client.get_current_padding()
            .map_err(|e| anyhow!("Failed to get padding: {}", e))? as usize;
        let available_frames = self.buffer_frame_count as usize - padding;

        if available_frames == 0 {
            return Ok(0);
        }

        let channels = format.channels as usize;
        let frames_to_write = (samples.len() / channels).min(available_frames);
        if frames_to_write == 0 {
            return Ok(0);
        }

        let samples_to_write = frames_to_write * channels;

        // SAFETY: Viewing f32 as u8 is always safe - u8 has alignment 1
        // and all bit patterns are valid.
        let byte_data = f32_as_bytes(&samples[..samples_to_write]);

        render_client.write_to_device(
            frames_to_write,
            byte_data,
            None,
        ).map_err(|e| anyhow!("Failed to write to device: {}", e))?;

        debug!("Rendered {} samples ({} frames)", samples_to_write, frames_to_write);
        Ok(samples_to_write)
    }
}

impl Drop for RenderStream {
    fn drop(&mut self) {
        let _ = self.stop();
    }
}

/// Find a device by its ID or name (strict matching)
fn find_device_by_id(device_id: &str, direction: Direction) -> Result<wasapi::Device> {
    // First pass: exact ID match
    let collection = DeviceCollection::new(&direction)
        .map_err(|e| anyhow!("Failed to get device collection: {}", e))?;

    for device in collection.into_iter() {
        let device = device.map_err(|e| anyhow!("Failed to enumerate device: {}", e))?;
        if let Ok(id) = device.get_id() {
            if id == device_id {
                info!("Found device by exact ID: {} ({})",
                      device.get_friendlyname().unwrap_or_default(), id);
                return Ok(device);
            }
        }
    }

    // Second pass: exact name match (case-insensitive)
    let collection = DeviceCollection::new(&direction)
        .map_err(|e| anyhow!("Failed to get device collection: {}", e))?;
    for device in collection.into_iter() {
        let device = device.map_err(|e| anyhow!("Failed to enumerate device: {}", e))?;
        if let Ok(name) = device.get_friendlyname() {
            if name.eq_ignore_ascii_case(device_id) {
                info!("Found device by exact name: {} ({})",
                      name, device.get_id().unwrap_or_default());
                return Ok(device);
            }
        }
    }

    // Third pass: partial name match (case-insensitive)
    let collection = DeviceCollection::new(&direction)
        .map_err(|e| anyhow!("Failed to get device collection: {}", e))?;
    for device in collection.into_iter() {
        let device = device.map_err(|e| anyhow!("Failed to enumerate device: {}", e))?;
        if let Ok(name) = device.get_friendlyname() {
            if name.to_lowercase().contains(&device_id.to_lowercase()) {
                warn!("Found device by partial name match: '{}' matched '{}'",
                      device_id, name);
                return Ok(device);
            }
        }
    }

    // List available devices for debugging
    let dir_name = if matches!(direction, Direction::Capture) { "capture" } else { "render" };
    let collection = DeviceCollection::new(&direction)
        .map_err(|e| anyhow!("Failed to get device collection: {}", e))?;
    let mut available = Vec::new();
    for device in collection.into_iter() {
        if let Ok(device) = device {
            let name = device.get_friendlyname().unwrap_or_default();
            let id = device.get_id().unwrap_or_default();
            available.push(format!("  '{}' ({})", name, id));
        }
    }

    Err(anyhow!(
        "Device not found: '{}'\nAvailable {} devices:\n{}",
        device_id, dir_name, available.join("\n")
    ))
}

/// Safely convert bytes to f32 samples (handles alignment correctly)
fn bytes_to_f32(bytes: &[u8], output: &mut [f32]) -> usize {
    let num_floats = bytes.len() / 4;
    let count = num_floats.min(output.len());
    for i in 0..count {
        let offset = i * 4;
        output[i] = f32::from_le_bytes([
            bytes[offset],
            bytes[offset + 1],
            bytes[offset + 2],
            bytes[offset + 3],
        ]);
    }
    count
}

/// View f32 slice as bytes (zero-copy, always safe since u8 has alignment 1)
fn f32_as_bytes(floats: &[f32]) -> &[u8] {
    unsafe {
        std::slice::from_raw_parts(
            floats.as_ptr() as *const u8,
            floats.len() * 4,
        )
    }
}
