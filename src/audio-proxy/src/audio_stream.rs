//! WASAPI audio stream management for capture and render

use anyhow::{anyhow, Context, Result};
use log::{debug, info};
use wasapi::{DeviceCollection, Direction, ShareMode};

/// Audio capture stream from a device (e.g., VB-Cable)
pub struct CaptureStream {
    device: wasapi::Device,
    client: Option<wasapi::AudioClient>,
    capture_client: Option<wasapi::AudioCaptureClient>,
    _channels: u16,
    started: bool,
}

impl CaptureStream {
    /// Create a new capture stream for the specified device
    pub fn new(device_id: &str, _sample_rate: u32, channels: u16) -> Result<Self> {
        info!("Creating capture stream for device: {}", device_id);

        // Find the device by ID
        let device = find_device_by_id(device_id, Direction::Capture)
            .context("Failed to find capture device")?;

        Ok(Self {
            device,
            client: None,
            capture_client: None,
            _channels: channels,
            started: false,
        })
    }

    /// Start capturing audio
    pub fn start(&mut self) -> Result<()> {
        if self.started {
            return Ok(());
        }

        // Create audio client
        let mut client = self.device.get_iaudioclient()
            .map_err(|e| anyhow!("Failed to get audio client: {}", e))?;

        // Get mix format from the device
        let wave_format = client.get_mixformat()
            .map_err(|e| anyhow!("Failed to get mix format: {}", e))?;

        info!("Capture format: {} Hz, {} channels",
              wave_format.get_samplespersec(),
              wave_format.get_nchannels());

        // Initialize for shared mode capture
        client.initialize_client(
            &wave_format,
            100_000, // 10ms buffer in 100ns units
            &Direction::Capture,
            &ShareMode::Shared,
            false, // not event driven for simplicity
        ).map_err(|e| anyhow!("Failed to initialize capture client: {}", e))?;

        // Get capture client
        let capture_client = client.get_audiocaptureclient()
            .map_err(|e| anyhow!("Failed to get capture client: {}", e))?;

        // Start the stream
        client.start_stream()
            .map_err(|e| anyhow!("Failed to start capture stream: {}", e))?;

        self.client = Some(client);
        self.capture_client = Some(capture_client);
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

    /// Read audio samples from the capture buffer
    /// Returns the number of samples read (samples = frames * channels)
    pub fn read(&mut self, buffer: &mut [f32]) -> Result<usize> {
        let capture_client = self.capture_client.as_mut()
            .ok_or_else(|| anyhow!("Capture client not initialized"))?;

        // Get available frames
        let available_frames = match capture_client.get_next_nbr_frames()
            .map_err(|e| anyhow!("Failed to get frame count: {}", e))? {
            Some(frames) => frames as usize,
            None => return Ok(0),
        };

        if available_frames == 0 {
            return Ok(0);
        }

        // Read the audio data into a byte buffer
        let bytes_per_frame = 8; // 2 channels * 4 bytes per float
        let mut byte_buffer = vec![0u8; available_frames * bytes_per_frame];
        let (frames_read, _flags) = capture_client.read_from_device(&mut byte_buffer)
            .map_err(|e| anyhow!("Failed to read from device: {}", e))?;

        // Convert bytes to f32 samples
        let actual_bytes = frames_read as usize * bytes_per_frame;
        let float_data = bytemuck_cast_slice(&byte_buffer[..actual_bytes]);
        let samples_read = float_data.len().min(buffer.len());
        buffer[..samples_read].copy_from_slice(&float_data[..samples_read]);

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
    _channels: u16,
    started: bool,
}

impl RenderStream {
    /// Create a new render stream for the specified device
    pub fn new(device_id: &str, _sample_rate: u32, channels: u16) -> Result<Self> {
        info!("Creating render stream for device: {}", device_id);

        // Find the device by ID
        let device = find_device_by_id(device_id, Direction::Render)
            .context("Failed to find render device")?;

        Ok(Self {
            device,
            client: None,
            render_client: None,
            buffer_frame_count: 0,
            _channels: channels,
            started: false,
        })
    }

    /// Start rendering audio
    pub fn start(&mut self) -> Result<()> {
        if self.started {
            return Ok(());
        }

        // Create audio client
        let mut client = self.device.get_iaudioclient()
            .map_err(|e| anyhow!("Failed to get audio client: {}", e))?;

        // Get mix format from the device
        let wave_format = client.get_mixformat()
            .map_err(|e| anyhow!("Failed to get mix format: {}", e))?;

        info!("Render format: {} Hz, {} channels",
              wave_format.get_samplespersec(),
              wave_format.get_nchannels());

        // Initialize for shared mode render
        client.initialize_client(
            &wave_format,
            100_000, // 10ms buffer in 100ns units
            &Direction::Render,
            &ShareMode::Shared,
            false, // not event driven for simplicity
        ).map_err(|e| anyhow!("Failed to initialize render client: {}", e))?;

        let buffer_frame_count = client.get_bufferframecount()
            .map_err(|e| anyhow!("Failed to get buffer frame count: {}", e))?;

        // Get render client
        let render_client = client.get_audiorenderclient()
            .map_err(|e| anyhow!("Failed to get render client: {}", e))?;

        // Start the stream
        client.start_stream()
            .map_err(|e| anyhow!("Failed to start render stream: {}", e))?;

        self.client = Some(client);
        self.render_client = Some(render_client);
        self.buffer_frame_count = buffer_frame_count;
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

    /// Write audio samples to the render buffer
    /// Returns the number of samples written
    pub fn write(&mut self, samples: &[f32]) -> Result<usize> {
        let client = self.client.as_ref()
            .ok_or_else(|| anyhow!("Client not initialized"))?;
        let render_client = self.render_client.as_mut()
            .ok_or_else(|| anyhow!("Render client not initialized"))?;

        // Get available space in buffer
        let padding = client.get_current_padding()
            .map_err(|e| anyhow!("Failed to get padding: {}", e))? as usize;
        let available_frames = self.buffer_frame_count as usize - padding;

        if available_frames == 0 {
            return Ok(0);
        }

        // Calculate frames to write based on actual channel count
        // Use 2 channels as default (stereo)
        let channels = 2usize;
        let frames_to_write = (samples.len() / channels).min(available_frames);
        if frames_to_write == 0 {
            return Ok(0);
        }

        let samples_to_write = frames_to_write * channels;

        // Convert f32 samples to bytes
        let byte_data = bytemuck_cast_to_bytes(&samples[..samples_to_write]);

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

/// Find a device by its ID
fn find_device_by_id(device_id: &str, direction: Direction) -> Result<wasapi::Device> {
    let collection = DeviceCollection::new(&direction)
        .map_err(|e| anyhow!("Failed to get device collection: {}", e))?;

    for device in collection.into_iter() {
        let device = device.map_err(|e| anyhow!("Failed to enumerate device: {}", e))?;
        if let Ok(id) = device.get_id() {
            // Check for exact match or partial match (for MMDevice IDs)
            if id == device_id || id.contains(device_id) || device_id.contains(&id) {
                info!("Found device: {} ({})", device.get_friendlyname().unwrap_or_default(), id);
                return Ok(device);
            }
        }
    }

    // If not found by ID, try by name
    let collection = DeviceCollection::new(&direction)
        .map_err(|e| anyhow!("Failed to get device collection: {}", e))?;
    for device in collection.into_iter() {
        let device = device.map_err(|e| anyhow!("Failed to enumerate device: {}", e))?;
        if let Ok(name) = device.get_friendlyname() {
            if name.to_lowercase().contains(&device_id.to_lowercase()) {
                info!("Found device by name: {} ({})", name, device.get_id().unwrap_or_default());
                return Ok(device);
            }
        }
    }

    Err(anyhow!("Device not found: {}", device_id))
}

// Safe byte casting utilities
fn bytemuck_cast_slice(bytes: &[u8]) -> &[f32] {
    // SAFETY: We know the WASAPI data is aligned and sized correctly for f32
    if bytes.len() % 4 != 0 {
        return &[];
    }
    unsafe {
        std::slice::from_raw_parts(
            bytes.as_ptr() as *const f32,
            bytes.len() / 4,
        )
    }
}

fn bytemuck_cast_to_bytes(floats: &[f32]) -> &[u8] {
    // SAFETY: f32 can always be safely viewed as bytes
    unsafe {
        std::slice::from_raw_parts(
            floats.as_ptr() as *const u8,
            floats.len() * 4,
        )
    }
}
