namespace DLSS_5_MANAGER.Services

open System
open Microsoft.Win32

/// Reads the machine's graphics card, driver, processor and Windows build.
///
/// Nothing here runs on its own. The community section shows a "Detect" button
/// and this is what it calls - the specs are never collected in the background
/// and never attached to a post the user did not choose to attach them to.
///
/// It deliberately avoids WMI: `System.Management` pulls in a dependency, and a
/// WMI query for the display adapter takes seconds on some machines. Everything
/// below is a registry read that returns immediately.
module SystemSpecs =

    [<CLIMutable>]
    type Specs =
        { Gpu: string
          Driver: string
          Cpu: string
          Os: string
          Ram: string }

    let empty = { Gpu = ""; Driver = ""; Cpu = ""; Os = ""; Ram = "" }

    let private readValue (key: RegistryKey) (name: string) =
        try
            match key.GetValue(name) with
            | null -> ""
            | v -> string v
        with _ ->
            ""

    /// NVIDIA's control panel number is the tail of the Windows driver version:
    /// "32.0.15.6164" is what the driver reports and "616.64" is what the user
    /// recognises. AMD and Intel show their own version unchanged.
    let private prettyDriver (vendorHint: string) (raw: string) =
        if String.IsNullOrWhiteSpace(raw) then ""
        elif not (vendorHint.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) then raw
        else
            let digits = raw.Replace(".", "")
            if digits.Length < 5 then raw
            else
                let tail = digits.Substring(digits.Length - 5)
                tail.Substring(0, 3) + "." + tail.Substring(3)

    /// The display adapters live under the display class GUID, one numbered
    /// subkey each. The first one with a driver description is the real card;
    /// the rest are usually virtual or remote-desktop adapters.
    let private readGpu () =
        try
            use root =
                Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"
                )

            if isNull root then ("", "")
            else
                let ignored =
                    [| "Basic Display"; "Remote Display"; "Virtual"; "Parsec"; "Meta "; "DisplayLink"; "Citrix" |]

                let candidates =
                    root.GetSubKeyNames()
                    |> Array.filter (fun n -> n.Length = 4 && n |> Seq.forall Char.IsDigit)
                    |> Array.choose (fun name ->
                        try
                            use sub = root.OpenSubKey(name)
                            if isNull sub then None
                            else
                                let desc = readValue sub "DriverDesc"
                                let ver = readValue sub "DriverVersion"
                                if String.IsNullOrWhiteSpace(desc) then None
                                elif ignored |> Array.exists (fun bad -> desc.Contains(bad, StringComparison.OrdinalIgnoreCase)) then None
                                else Some(desc, ver)
                        with _ ->
                            None)

                // A laptop answers with both its integrated chip and its real
                // card, and the game runs on the real card. Ask for the vendors
                // in order rather than taking whichever the registry listed
                // first, or an RTX machine reports its iGPU to the community.
                let preferred =
                    [| [| "GeForce"; "NVIDIA"; "Quadro" |]
                       [| "Radeon"; "AMD " |]
                       [| "Arc" |]
                       [| "Intel" |] |]
                    |> Array.tryPick (fun vendor ->
                        candidates
                        |> Array.tryFind (fun (desc, _) ->
                            vendor |> Array.exists (fun v -> desc.Contains(v, StringComparison.OrdinalIgnoreCase))))

                match preferred |> Option.orElse (Array.tryHead candidates) with
                | Some(desc, ver) -> (desc.Trim(), prettyDriver desc ver)
                | None -> ("", "")
        with _ ->
            ("", "")

    let private readCpu () =
        try
            use key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0")
            if isNull key then "" else (readValue key "ProcessorNameString").Trim()
        with _ ->
            ""

    /// "Windows 11 Pro 24H2 · 64-bit · 26200".
    ///
    /// Two traps live here. `ProductName` still reads "Windows 10 Pro" on every
    /// Windows 11 machine - Microsoft never updated it, and the only honest
    /// test is build >= 22000. And `Environment.OSVersion` reports 10.0 for 11
    /// as well, so the build number is what identifies the release.
    let private readOs () =
        try
            use key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion")

            let build =
                match key with
                | null -> Environment.OSVersion.Version.Build
                | k ->
                    match Int32.TryParse(readValue k "CurrentBuildNumber") with
                    | true, n -> n
                    | _ -> Environment.OSVersion.Version.Build

            let product =
                let raw = if isNull key then "" else readValue key "ProductName"

                if String.IsNullOrWhiteSpace(raw) then
                    if build >= 22000 then "Windows 11" else "Windows 10"
                elif build >= 22000 then
                    raw.Replace("Windows 10", "Windows 11")
                else
                    raw

            // "24H2" and friends. Older builds only have ReleaseId.
            let release =
                if isNull key then ""
                else
                    let display = readValue key "DisplayVersion"
                    if String.IsNullOrWhiteSpace(display) then readValue key "ReleaseId" else display

            let bits = if Environment.Is64BitOperatingSystem then "64-bit" else "32-bit"

            [| product.Trim(); release.Trim(); bits; string build |]
            |> Array.filter (fun p -> not (String.IsNullOrWhiteSpace(p)))
            |> String.concat " · "
        with _ ->
            if Environment.Is64BitOperatingSystem then "Windows · 64-bit" else "Windows · 32-bit"

    [<System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)>]
    type private MEMORYSTATUSEX =
        struct
            val mutable dwLength: uint32
            val mutable dwMemoryLoad: uint32
            val mutable ullTotalPhys: uint64
            val mutable ullAvailPhys: uint64
            val mutable ullTotalPageFile: uint64
            val mutable ullAvailPageFile: uint64
            val mutable ullTotalVirtual: uint64
            val mutable ullAvailVirtual: uint64
            val mutable ullAvailExtendedVirtual: uint64
        end

    [<System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)>]
    extern bool private GlobalMemoryStatusEx(MEMORYSTATUSEX& buffer)

    /// "32 GB RAM". Rounded to the nearest sensible stick total, because the
    /// figure Windows reports is always a little under what is installed - some
    /// of it is reserved before Windows ever sees it.
    let private readRam () =
        try
            let mutable status = MEMORYSTATUSEX()
            status.dwLength <- uint32 (System.Runtime.InteropServices.Marshal.SizeOf<MEMORYSTATUSEX>())

            if GlobalMemoryStatusEx(&status) then
                let gb = float status.ullTotalPhys / 1073741824.0
                let common = [| 2.0; 4.0; 6.0; 8.0; 12.0; 16.0; 24.0; 32.0; 48.0; 64.0; 96.0; 128.0; 192.0; 256.0 |]

                match common |> Array.tryFind (fun c -> gb <= c + 0.4 && gb >= c - 1.2) with
                | Some c -> sprintf "%g GB RAM" c
                | None -> sprintf "%.0f GB RAM" (Math.Round(gb))
            else
                ""
        with _ ->
            ""

    /// One pass over the registry. Safe to call from a background thread.
    let detect () =
        let gpu, driver = readGpu ()

        { Gpu = gpu
          Driver = driver
          Cpu = readCpu ()
          Os = readOs ()
          Ram = readRam () }
