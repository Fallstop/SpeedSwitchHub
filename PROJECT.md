\### \*\*Project Name:\*\* G-AutoSwitch (Draft)



\### \*\*1. Executive Summary\*\*



A lightweight Windows system tray application designed to automate audio output routing for the Logitech G Pro X 2 Lightspeed. The application resolves two specific hardware/software limitations: the inability of Windows to detect when the headset is physically powered off (due to the always-active USB dongle) and the failure of running 3D applications (games) to recognize "Default Device" changes mid-session.



---



\### \*\*2. The Problem Statement\*\*



Users switching between wireless play (USB Dongle) and wired play (3.5mm Analog) face significant friction:



1\. \*\*The "Ghost" Device:\*\* The Logitech USB dongle remains active even when the headset is powered off or connected via 3.5mm wire. Windows continues to route audio to the silent wireless channel, requiring manual intervention.

2\. \*\*Application Persistence:\*\* Changing the Windows "Default Playback Device" does not affect running games or applications that have already initialized their audio engine (e.g., \*Cyberpunk 2077\*, \*Call of Duty\*). These apps hold a handle to the original device ID, forcing the user to restart the game to get audio on the new source.



---



\### \*\*3. Technical Objectives\*\*



\* \*\*True State Detection:\*\* Determine the actual power/link state of the headset, bypassing the generic "Connected" status reported by the USB dongle.

\* \*\*Live Stream Migration:\*\* Forcefully migrate active audio sessions (processes) from one audio endpoint to another without restarting the application.

\* \*\*Zero Latency Overhead:\*\* Avoid virtual cable drivers (VB-Cable/Virtual Audio Cable) to maintain the native hardware audio path for competitive gaming.



---



\### \*\*4. Solution Architecture\*\*



\#### \*\*Module A: The Hardware Poller (HID++ Implementation)\*\*



Since the USB-C port is charge-only, the system cannot detect the 3.5mm cable insertion. Instead, we infer the wired state by detecting the \*absence\* of the wireless link.



\* \*\*Protocol:\*\* Logitech HID++ 2.0.

\* \*\*Mechanism:\*\* The app sends periodic "Heartbeat" or "Battery Level" queries to the USB Dongle (VID: `0x046D`).

\* \*\*Logic:\*\*

\* \*Response received:\* Headset is \*\*ON\*\* (Wireless Mode).

\* \*Timeout/Offline Error:\* Headset is \*\*OFF\*\* (Assumed Wired Mode).







\#### \*\*Module B: The Audio Director (IAudioPolicyConfig)\*\*



Standard .NET APIs cannot move active audio streams. We will utilize the undocumented Windows COM interface `IAudioPolicyConfig` to manipulate the Windows Audio Session API (WASAPI).



\* \*\*Mechanism:\*\*

1\. Enumerate all active Audio Sessions (processes emitting sound).

2\. Identify sessions attached to the "Stale" device.

3\. Call `SetPersistedDefaultAudioEndpoint` for each Process ID (PID) to hot-swap the stream to the new target.







---



\### \*\*5. Technical Stack\*\*



\* \*\*Language:\*\* C# / .NET 6+ (for modern COM interop support).

\* \*\*UI Framework:\*\* WPF (Windows Presentation Foundation) for a modern, hardware-accelerated System Tray UI.

\* \*\*Key Libraries:\*\*

\* `HidLibrary`: For raw USB communication with the Logitech Dongle.

\* `CoreAudioApi` (or custom Interop): For session enumeration.

\* `P/Invoke`: For `IAudioPolicyConfig` (UUIDs vary by Windows Build).







---



\### \*\*6. Risks \& Constraints\*\*



\* \*\*G Hub Collision:\*\* Querying the dongle while Logitech G Hub is running may cause resource contention. The app must implement robust error handling or "Share Mode" access to the HID device.

\* \*\*Windows Updates:\*\* The `IAudioPolicyConfig` Interface ID (IID) is undocumented and changes between Windows versions (e.g., Windows 10 21H2 vs. Windows 11). The application must detect the OS version and select the correct GUID strategy at runtime.

\* \*\*Anti-Cheat Software:\*\* While rare for audio, injecting instructions into audio streams of protected processes (like \*Valorant\* or \*Apex Legends\*) must be tested to ensure it does not trigger anti-cheat violations. (Note: Using OS-level volume mixer APIs usually bypasses this risk).



---



\### \*\*7. User Flow\*\*



1\. \*\*Setup:\*\* User selects "Wireless Endpoint" (Logitech G Pro X 2) and "Wired Endpoint" (Realtek/DAC) in the UI.

2\. \*\*Running:\*\* App sits in the tray.

3\. \*\*Event:\*\* User turns off the headset to plug in the 3.5mm cable.

4\. \*\*Action:\*\* App detects HID timeout -> Switches Default Device to Realtek -> Migrates active Game Audio to Realtek.

5\. \*\*Result:\*\* Audio continues playing on speakers/wired headphones instantly.





