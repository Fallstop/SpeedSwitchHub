//! Lock-free ring buffer for low-latency audio transfer between threads

use std::sync::atomic::{AtomicUsize, Ordering};

/// A lock-free single-producer single-consumer ring buffer for audio samples
pub struct AudioRingBuffer {
    buffer: Box<[f32]>,
    capacity: usize,
    write_pos: AtomicUsize,
    read_pos: AtomicUsize,
}

impl AudioRingBuffer {
    /// Create a new ring buffer with the specified capacity (in samples)
    pub fn new(capacity: usize) -> Self {
        // Round up to power of 2 for efficient modulo operations
        let capacity = capacity.next_power_of_two();

        Self {
            buffer: vec![0.0f32; capacity].into_boxed_slice(),
            capacity,
            write_pos: AtomicUsize::new(0),
            read_pos: AtomicUsize::new(0),
        }
    }

    /// Write samples to the buffer
    /// Returns the number of samples actually written (may be less if buffer is full)
    pub fn write(&self, samples: &[f32]) -> usize {
        let write_pos = self.write_pos.load(Ordering::Acquire);
        let read_pos = self.read_pos.load(Ordering::Acquire);

        // Calculate available space
        let available = if write_pos >= read_pos {
            self.capacity - (write_pos - read_pos) - 1
        } else {
            read_pos - write_pos - 1
        };

        let to_write = samples.len().min(available);
        if to_write == 0 {
            return 0;
        }

        // Get mutable access to buffer through raw pointer (safe due to SPSC design)
        let buffer_ptr = self.buffer.as_ptr() as *mut f32;

        for i in 0..to_write {
            let idx = (write_pos + i) & (self.capacity - 1);
            unsafe {
                *buffer_ptr.add(idx) = samples[i];
            }
        }

        // Update write position with release ordering
        let new_write_pos = (write_pos + to_write) & (self.capacity - 1);
        self.write_pos.store(new_write_pos, Ordering::Release);

        to_write
    }

    /// Read samples from the buffer
    /// Returns the number of samples actually read (may be less if buffer doesn't have enough)
    pub fn read(&self, samples: &mut [f32]) -> usize {
        let write_pos = self.write_pos.load(Ordering::Acquire);
        let read_pos = self.read_pos.load(Ordering::Acquire);

        // Calculate available samples
        let available = if write_pos >= read_pos {
            write_pos - read_pos
        } else {
            self.capacity - (read_pos - write_pos)
        };

        let to_read = samples.len().min(available);
        if to_read == 0 {
            return 0;
        }

        for i in 0..to_read {
            let idx = (read_pos + i) & (self.capacity - 1);
            samples[i] = self.buffer[idx];
        }

        // Update read position with release ordering
        let new_read_pos = (read_pos + to_read) & (self.capacity - 1);
        self.read_pos.store(new_read_pos, Ordering::Release);

        to_read
    }

    /// Get the number of samples currently in the buffer
    pub fn len(&self) -> usize {
        let write_pos = self.write_pos.load(Ordering::Acquire);
        let read_pos = self.read_pos.load(Ordering::Acquire);

        if write_pos >= read_pos {
            write_pos - read_pos
        } else {
            self.capacity - (read_pos - write_pos)
        }
    }

    /// Check if the buffer is empty
    pub fn is_empty(&self) -> bool {
        self.len() == 0
    }

    /// Get the capacity of the buffer
    pub fn capacity(&self) -> usize {
        self.capacity - 1 // One slot is always kept empty
    }

    /// Clear the buffer
    pub fn clear(&self) {
        self.read_pos.store(0, Ordering::Release);
        self.write_pos.store(0, Ordering::Release);
    }
}

// SAFETY: AudioRingBuffer is designed for single-producer single-consumer use
// The atomics ensure proper synchronization between the producer and consumer threads
unsafe impl Send for AudioRingBuffer {}
unsafe impl Sync for AudioRingBuffer {}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_write_read() {
        let buffer = AudioRingBuffer::new(16);

        let samples = [1.0, 2.0, 3.0, 4.0];
        assert_eq!(buffer.write(&samples), 4);
        assert_eq!(buffer.len(), 4);

        let mut output = [0.0f32; 4];
        assert_eq!(buffer.read(&mut output), 4);
        assert_eq!(output, samples);
        assert!(buffer.is_empty());
    }

    #[test]
    fn test_overflow() {
        let buffer = AudioRingBuffer::new(4); // Rounds up to 4, capacity is 3

        let samples = [1.0, 2.0, 3.0, 4.0, 5.0];
        let written = buffer.write(&samples);
        assert!(written < samples.len());
    }

    #[test]
    fn test_underflow() {
        let buffer = AudioRingBuffer::new(16);

        let samples = [1.0, 2.0];
        buffer.write(&samples);

        let mut output = [0.0f32; 4];
        assert_eq!(buffer.read(&mut output), 2);
    }
}
