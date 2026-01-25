\### \*\*Project Architecture Document: G-AutoSwitch\*\*



\*\*Core Objective:\*\* Automate "Default Audio Device" switching based on the hardware link status of the Logitech G Pro X 2, bypassing the always-connected USB dongle limitation.



\*\*Tech Stack:\*\*



\* \*\*IDE:\*\* JetBrains Rider

\* \*\*Framework:\*\* .NET 10

\* \*\*UI:\*\* WinUI 3 (Windows App SDK)

\* \*\*Pattern:\*\* MVVM (CommunityToolkit.Mvvm)

\* \*\*IPC/Hardware:\*\* HID++ (via `HidLibrary`), COM Interop (CoreAudio)



---



\### \*\*Stage 1: Project Initialization \& Scaffold\*\*



Since Rider does not yet natively support WinUI 3 "File > New" templates as seamlessly as Visual Studio, we will initialize via CLI to ensure the `.csproj` and `package.appxmanifest` are correctly configured for the Windows App SDK.



\* \*\*Action:\*\* Install the latest WinUI 3 templates: `dotnet new install Microsoft.WindowsAppSDK.Templates`.

\* \*\*Execution:\*\* Create a blank \*\*"WinUI 3 App (Packaged)"\*\* solution.

\* \*\*Structure Setup:\*\*

\* Create a `Core` project (Class Library) for business logic and interfaces.

\* Create a `Hardware` project (Class Library) for HID interaction.

\* Keep the `UI` project strictly for XAML/View layers.





\* \*\*Dependencies:\*\* Add `CommunityToolkit.Mvvm` and `Microsoft.Windows.SDK.BuildTools` to the solution.



\### \*\*Stage 2: The User Interface (WinUI 3)\*\*



We need a configuration surface. Since this app runs mostly in the background, the Main Window is essentially a "Settings Panel."



\* \*\*Architecture:\*\*

\* \*\*MVVM Pattern:\*\* Strict separation. `MainViewModel` holds the state of selected devices.

\* \*\*Device Enumeration:\*\* The UI must bind to a `ObservableCollection<AudioDevice>`.

\* \*\*Selection Logic:\*\* Two distinct ComboBoxes:

\* \*Wireless Target:\* Filtered to Logitech devices (optional but helpful).

\* \*Wired Target:\* Realtek/Auxiliary outputs.









\* \*\*Data Persistence:\*\* Implement `SettingsService` that saves the GUIDs of the selected devices to a local JSON file (e.g., in `%AppData%`).

\* \*\*Constraint:\*\* The UI must handle "Device Not Found" gracefully (e.g., if the user unplugs the external DAC used for wired mode).



\### \*\*Stage 3: The Tray Agent (Background Lifecycle)\*\*



WinUI 3 applications are desktop apps by default. We need to "minimize to tray" rather than close.



\* \*\*Library Strategy:\*\* Use \*\*`H.NotifyIcon.WinUI`\*\* (or similar community wrapper) because native WinUI 3 tray support is verbose and complex.

\* \*\*Lifecycle Logic:\*\*

\* Override the "Close" window event (`AppWindow.Closing`) to `Hide()` the window instead of terminating the process.

\* \*\*Tray Context Menu:\*\*

\* "Open Settings" (Restores the Window).

\* "Exit" (Actually kills the process).





\* \*\*Autostart:\*\* Implement Registry key management (`HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run`) to launch the executable silently on boot (`/silent` arg).







\### \*\*Stage 4: The Audio Engine (COM Interop)\*\*



This is the "Aggressive Switcher." It must bypass standard .NET APIs and talk directly to the Windows Component Object Model.



\* \*\*Windows Version Adapter Pattern:\*\*

\* Create an interface `IAudioSwitcher`.

\* Implement `Win11AudioSwitcher` and `Win10AudioSwitcher`.

\* \*Why?\* The `IAudioPolicyConfig` Interface ID (IID) differs between Windows versions. The factory must detect the OS build and instantiate the correct COM wrapper.





\* \*\*Functionality:\*\*

1\. \*\*Global Default Switch:\*\* Change the OS-level Default Multimedia Device.

2\. \*\*Session Migration (The "Aggressive" part):\*\*

\* Use `IAudioSessionManager2` to enumerate active audio sessions (games/Spotify).

\* Identify sessions "stuck" on the old device.

\* Force-call `SetPersistedDefaultAudioEndpoint` on those Process IDs.











\### \*\*Stage 5: Hardware Detection (HID++ Console Sandbox)\*\*



This is the most volatile part of the project (reverse engineering). We will isolate this initially.



\* \*\*Sandbox App:\*\* Create a separate `.NET 10 Console Application` strictly for probing.

\* \*\*HID Logic:\*\*

\* \*\*Enumeration:\*\* Scan USB VID `0x046D` (Logitech).

\* \*\*Handshake:\*\* Send the HID++ 2.0 Root Notification (`0x10...`) to verify the dongle accepts commands.

\* \*\*Feature Discovery:\*\* Query the dongle for the `BatteryStatus` feature index.

\* \*\*Polling Loop:\*\* Send a "Get Battery" command every 2000ms.

\* \*\*Interpretation:\*\* If the dongle returns "Device Offline" or fails to ACK, we assume \*\*Wired Mode\*\*. If it returns a battery %, we assume \*\*Wireless Mode\*\*.





\* \*\*Integration:\*\* Once the byte sequences are confirmed working, move this logic into the main `Hardware` library.



\### \*\*Stage 6: The "Brain" (State Machine \& Rules)\*\*



This brings the projects together.



\* \*\*State Machine:\*\*

\* States: `WirelessConnected`, `WirelessDisconnected`, `Transitioning`.





\* \*\*Debounce Logic:\*\* To prevent rapid audio flipping if the signal is weak, implement a "Confidence Timer" (e.g., \*Headset must be disconnected for > 3 seconds before switching\*).

\* \*\*Activity Gate (The "Limit" Requirement):\*\*

\* Before switching, check `IAudioSessionManager2`.

\* \*Rule:\* "Only switch if audio is actually playing." (Prevents ghost switching at 3 AM).

\* \*Rule:\* "Do not switch if the target device is missing."







