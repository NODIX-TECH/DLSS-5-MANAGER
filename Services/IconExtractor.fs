namespace DLSS_5_MANAGER.Services

open System
open System.IO
open System.Drawing
open System.Drawing.Imaging
open System.Runtime.InteropServices
open System.Security.Cryptography
open System.Text

module IconExtractor =

    /// ExtractAssociatedIcon only ever hands back the 32x32 entry, which looks
    /// like mush once a card scales it up. PrivateExtractIcons lets us ask for
    /// a specific size, so we can take the largest one the executable actually
    /// carries - modern games ship 256x256.
    [<DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)>]
    extern int PrivateExtractIconsW(string lpszFile, int nIconIndex, int cxIcon, int cyIcon, IntPtr[] phicon, int[] piconid, int nIcons, uint flags)

    [<DllImport("user32.dll", SetLastError = true)>]
    extern bool DestroyIcon(IntPtr hIcon)

    /// Largest first: the first size that comes back is the best available.
    let private preferredSizes = [ 256; 128; 96; 64; 48; 32 ]

    /// Pulls the sharpest icon out of an executable as a bitmap the caller owns.
    let private extractBestBitmap (exePath: string) : Bitmap option =
        let mutable result = None

        for size in preferredSizes do
            if result.IsNone then
                let handles = Array.zeroCreate<IntPtr> 1
                let ids = Array.zeroCreate<int> 1

                let count =
                    try PrivateExtractIconsW(exePath, 0, size, size, handles, ids, 1, 0u)
                    with _ -> 0

                if count > 0 && handles.[0] <> IntPtr.Zero then
                    try
                        use icon = Icon.FromHandle(handles.[0])
                        // ToBitmap copies, so the bitmap outlives the handle.
                        result <- Some(icon.ToBitmap())
                    with _ ->
                        ()

                    // FromHandle does not take ownership; the handle is ours to free.
                    try DestroyIcon(handles.[0]) |> ignore with _ -> ()

        result

    let private getCacheDir () =
        let localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        let cachePath = Path.Combine(localAppData, "DLSS5Manager", "Cache", "Icons")
        if not (Directory.Exists(cachePath)) then
            Directory.CreateDirectory(cachePath) |> ignore
        cachePath

    let private computeHash (input: string) =
        use sha = SHA256.Create()
        let bytes = Encoding.UTF8.GetBytes(input)
        let hash = sha.ComputeHash(bytes)
        BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant()

    let extractIconToCache (exePath: string) : string =
        try
            if String.IsNullOrWhiteSpace(exePath) || not (File.Exists(exePath)) then
                ""
            else
                let cacheDir = getCacheDir ()
                let hash = computeHash exePath

                // The "_hq" suffix is what retires the 32x32 icons cached by
                // earlier builds - they would otherwise be reused forever and
                // the sharper extraction would never be seen.
                let targetPng = Path.Combine(cacheDir, sprintf "%s_hq.png" hash)

                if File.Exists(targetPng) then
                    targetPng
                else
                    let legacyPng = Path.Combine(cacheDir, sprintf "%s.png" hash)
                    try
                        if File.Exists(legacyPng) then File.Delete(legacyPng)
                    with _ -> ()

                    match extractBestBitmap exePath with
                    | Some bmp ->
                        use bmp = bmp
                        bmp.Save(targetPng, ImageFormat.Png)
                        targetPng
                    | None ->
                        // Nothing at any size - fall back to the 32x32 shell icon.
                        use sysIcon = Icon.ExtractAssociatedIcon(exePath)

                        if sysIcon <> null then
                            use bmp = sysIcon.ToBitmap()
                            bmp.Save(targetPng, ImageFormat.Png)
                            targetPng
                        else
                            ""
        with
        | _ -> ""
