//! IPC communication via named pipes for controlling the audio proxy

use std::ffi::OsStr;
use std::os::windows::ffi::OsStrExt;
use std::time::Duration;

use anyhow::{anyhow, Context, Result};
use log::{debug, info};
use serde::{Deserialize, Serialize};
use windows::core::PCWSTR;
use windows::Win32::Foundation::{CloseHandle, HANDLE, INVALID_HANDLE_VALUE, GENERIC_READ, GENERIC_WRITE};
use windows::Win32::Storage::FileSystem::{
    CreateFileW, ReadFile, WriteFile, FILE_SHARE_NONE, OPEN_EXISTING, PIPE_ACCESS_DUPLEX,
};
use windows::Win32::System::Pipes::{
    ConnectNamedPipe, CreateNamedPipeW, DisconnectNamedPipe, SetNamedPipeHandleState,
    PIPE_READMODE_MESSAGE, PIPE_TYPE_MESSAGE, PIPE_UNLIMITED_INSTANCES, PIPE_WAIT,
};

/// Named pipe path for IPC
pub const PIPE_NAME: &str = r"\\.\pipe\GAutoSwitchAudioProxy";

/// Commands that can be sent to the audio proxy
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(tag = "command", content = "data")]
pub enum IpcCommand {
    /// Set the speaker output device
    SetOutput { device_id: String },
    /// Get the current status
    GetStatus,
    /// Stop the proxy
    Stop,
    /// Set the microphone input device (hot-swap physical mic)
    SetMicInput { device_id: String },
    /// Enable or disable the microphone proxy
    EnableMic { enabled: bool },
}

/// Response from the audio proxy
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct IpcResponse {
    pub success: bool,
    pub message: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub running: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub output_device: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub mic_enabled: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub mic_input_device: Option<String>,
}

impl IpcResponse {
    pub fn success(message: &str) -> Self {
        Self {
            success: true,
            message: message.to_string(),
            running: None,
            output_device: None,
            mic_enabled: None,
            mic_input_device: None,
        }
    }

    pub fn error(message: &str) -> Self {
        Self {
            success: false,
            message: message.to_string(),
            running: None,
            output_device: None,
            mic_enabled: None,
            mic_input_device: None,
        }
    }

    pub fn status(running: bool, output_device: &str) -> Self {
        Self {
            success: true,
            message: "Status retrieved".to_string(),
            running: Some(running),
            output_device: Some(output_device.to_string()),
            mic_enabled: None,
            mic_input_device: None,
        }
    }

    pub fn status_full(
        running: bool,
        output_device: &str,
        mic_enabled: bool,
        mic_input_device: Option<&str>,
    ) -> Self {
        Self {
            success: true,
            message: "Status retrieved".to_string(),
            running: Some(running),
            output_device: Some(output_device.to_string()),
            mic_enabled: Some(mic_enabled),
            mic_input_device: mic_input_device.map(|s| s.to_string()),
        }
    }
}

/// Named pipe server for receiving commands
pub struct IpcServer {
    pipe_handle: HANDLE,
    connected: bool,
}

impl IpcServer {
    /// Create a new IPC server
    pub fn new() -> Result<Self> {
        let pipe_name = to_wide_string(PIPE_NAME);

        let handle = unsafe {
            CreateNamedPipeW(
                PCWSTR(pipe_name.as_ptr()),
                PIPE_ACCESS_DUPLEX,
                PIPE_TYPE_MESSAGE | PIPE_READMODE_MESSAGE | PIPE_WAIT,
                PIPE_UNLIMITED_INSTANCES,
                4096,
                4096,
                0,
                None,
            )
        };

        if handle == INVALID_HANDLE_VALUE {
            return Err(anyhow!("Failed to create named pipe"));
        }

        Ok(Self {
            pipe_handle: handle,
            connected: false,
        })
    }

    /// Accept a connection and receive a command with timeout
    pub fn accept_with_timeout(&mut self, _timeout: Duration) -> Result<Option<IpcCommand>> {
        if !self.connected {
            // Wait for a client to connect
            let result = unsafe { ConnectNamedPipe(self.pipe_handle, None) };
            if result.is_err() {
                // If error is ERROR_PIPE_CONNECTED, a client connected before we called ConnectNamedPipe
                let err = std::io::Error::last_os_error();
                if err.raw_os_error() != Some(535) {
                    // ERROR_PIPE_CONNECTED = 535
                    return Ok(None);
                }
            }
            self.connected = true;
            debug!("Client connected to IPC pipe");
        }

        // Read command from pipe
        let mut buffer = [0u8; 4096];
        let mut bytes_read = 0u32;

        let result = unsafe {
            ReadFile(
                self.pipe_handle,
                Some(&mut buffer),
                Some(&mut bytes_read),
                None,
            )
        };

        if result.is_err() || bytes_read == 0 {
            // Client disconnected
            self.disconnect();
            return Ok(None);
        }

        let data = &buffer[..bytes_read as usize];
        let command: IpcCommand = serde_json::from_slice(data)
            .context("Failed to parse IPC command")?;

        debug!("Received IPC command: {:?}", command);
        Ok(Some(command))
    }

    /// Send a response to the client
    pub fn send_response(&mut self, response: &IpcResponse) -> Result<()> {
        if !self.connected {
            return Err(anyhow!("Not connected to client"));
        }

        let data = serde_json::to_vec(response)?;
        let mut bytes_written = 0u32;

        let result = unsafe {
            WriteFile(
                self.pipe_handle,
                Some(&data),
                Some(&mut bytes_written),
                None,
            )
        };

        if result.is_err() {
            self.disconnect();
            return Err(anyhow!("Failed to write to pipe"));
        }

        // Disconnect after response to allow next client
        self.disconnect();

        Ok(())
    }

    fn disconnect(&mut self) {
        if self.connected {
            unsafe {
                let _ = DisconnectNamedPipe(self.pipe_handle);
            }
            self.connected = false;
            debug!("Client disconnected from IPC pipe");
        }
    }
}

impl Drop for IpcServer {
    fn drop(&mut self) {
        self.disconnect();
        unsafe {
            let _ = CloseHandle(self.pipe_handle);
        }
    }
}

/// Named pipe client for sending commands
pub struct IpcClient {
    pipe_handle: HANDLE,
}

impl IpcClient {
    /// Connect to the IPC server
    pub fn connect() -> Result<Self> {
        let pipe_name = to_wide_string(PIPE_NAME);

        let handle = unsafe {
            CreateFileW(
                PCWSTR(pipe_name.as_ptr()),
                (GENERIC_READ | GENERIC_WRITE).0,
                FILE_SHARE_NONE,
                None,
                OPEN_EXISTING,
                Default::default(),
                None,
            )
        }.map_err(|e| anyhow!("Failed to connect to named pipe: {}", e))?;

        if handle == INVALID_HANDLE_VALUE {
            return Err(anyhow!("Failed to connect to named pipe"));
        }

        // Set pipe to message mode
        let mut mode = PIPE_READMODE_MESSAGE;
        unsafe {
            SetNamedPipeHandleState(handle, Some(&mut mode), None, None)
                .map_err(|e| anyhow!("Failed to set pipe mode: {}", e))?;
        }

        Ok(Self { pipe_handle: handle })
    }

    /// Send a command and receive a response
    pub fn send_command(&mut self, command: &IpcCommand) -> Result<IpcResponse> {
        let data = serde_json::to_vec(command)?;
        let mut bytes_written = 0u32;

        unsafe {
            WriteFile(
                self.pipe_handle,
                Some(&data),
                Some(&mut bytes_written),
                None,
            ).map_err(|e| anyhow!("Failed to write to pipe: {}", e))?;
        }

        // Read response
        let mut buffer = [0u8; 4096];
        let mut bytes_read = 0u32;

        unsafe {
            ReadFile(
                self.pipe_handle,
                Some(&mut buffer),
                Some(&mut bytes_read),
                None,
            ).map_err(|e| anyhow!("Failed to read from pipe: {}", e))?;
        }

        let response: IpcResponse = serde_json::from_slice(&buffer[..bytes_read as usize])?;
        Ok(response)
    }
}

impl Drop for IpcClient {
    fn drop(&mut self) {
        unsafe {
            let _ = CloseHandle(self.pipe_handle);
        }
    }
}

/// Convert a string to a null-terminated wide string
fn to_wide_string(s: &str) -> Vec<u16> {
    OsStr::new(s)
        .encode_wide()
        .chain(std::iter::once(0))
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_command_serialization() {
        let cmd = IpcCommand::SetOutput {
            device_id: "test-device".to_string(),
        };
        let json = serde_json::to_string(&cmd).unwrap();
        let parsed: IpcCommand = serde_json::from_str(&json).unwrap();

        match parsed {
            IpcCommand::SetOutput { device_id } => assert_eq!(device_id, "test-device"),
            _ => panic!("Wrong command type"),
        }
    }

    #[test]
    fn test_response_serialization() {
        let resp = IpcResponse::status(true, "device-123");
        let json = serde_json::to_string(&resp).unwrap();
        let parsed: IpcResponse = serde_json::from_str(&json).unwrap();

        assert!(parsed.success);
        assert_eq!(parsed.running, Some(true));
        assert_eq!(parsed.output_device, Some("device-123".to_string()));
    }
}
