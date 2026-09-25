namespace DLSS_5_MANAGER.Services

open System
open System.IO
open System.Security.Cryptography
open System.Text
open SkiaSharp

/// Images for PULSE, the in-app chat.
///
/// Everything is turned into WebP on this machine before it is uploaded. A
/// phone screenshot arrives as a 4 MB PNG and leaves as roughly 150 KB, which
/// is what keeps the R2 bucket light and the upload quick. SkiaSharp is already
/// in the process - Avalonia renders with it - so this adds no dependency.
///
/// Decoding covers what people actually paste: JPEG, PNG, GIF (first frame),
/// BMP, WebP and ICO.
module ChatImages =

    /// Nothing larger is even opened. A 25 MB image is almost certainly not a
    /// screenshot, and decoding one would stall the machine for no reason.
    [<Literal>]
    let MaxSourceBytes = 25L * 1024L * 1024L

    /// The server refuses anything bigger, so the encoder aims under this.
    [<Literal>]
    let MaxUploadBytes = 1_500_000

    /// Long side, in pixels. A chat bubble never shows more than this, and it
    /// is still sharp enough to read text in a screenshot of a game menu.
    [<Literal>]
    let MaxSide = 1600

    type Encoded =
        { Bytes: byte[]
          Width: int
          Height: int }

    let private scaled (bitmap: SKBitmap) (maxSide: int) : SKBitmap =
        let longest = max bitmap.Width bitmap.Height

        if longest <= maxSide then
            bitmap
        else
            let factor = float maxSide / float longest
            let w = max 1 (int (Math.Round(float bitmap.Width * factor)))
            let h = max 1 (int (Math.Round(float bitmap.Height * factor)))
            let info = SKImageInfo(w, h, bitmap.ColorType, bitmap.AlphaType)
            let sampling = SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)

            match bitmap.Resize(info, sampling) with
            | null -> bitmap
            | resized -> resized

    let private encode (bitmap: SKBitmap) (quality: int) : byte[] =
        use image = SKImage.FromBitmap(bitmap)
        use data = image.Encode(SKEncodedImageFormat.Webp, quality)
        if isNull data then [||] else data.ToArray()

    /// Turns a decoded picture into an upload-sized WebP.
    ///
    /// Quality steps down, then size does, until it fits - a noisy photo at
    /// full resolution can miss the limit at the first quality where a
    /// screenshot lands a tenth of the way under it.
    let private toUpload (original: SKBitmap) : Result<Encoded, string> =
        if isNull original then
            Error "That is not a picture this app can read."
        else
            let attempts =
                [ MaxSide, 82
                  MaxSide, 68
                  MaxSide, 55
                  1200, 60
                  900, 55 ]

            let rec attempt remaining =
                match remaining with
                | [] -> Error "That picture is too detailed to send, even made smaller."
                | (side, quality) :: rest ->
                    let working = scaled original side

                    try
                        let bytes = encode working quality

                        if bytes.Length = 0 then
                            Error "The picture could not be converted."
                        elif bytes.Length <= MaxUploadBytes then
                            Ok
                                { Bytes = bytes
                                  Width = working.Width
                                  Height = working.Height }
                        else
                            attempt rest
                    finally
                        if not (Object.ReferenceEquals(working, original)) then
                            working.Dispose()

            attempt attempts

    /// A picture picked from disk.
    let fromFile (path: string) : Result<Encoded, string> =
        try
            let info = FileInfo(path)

            if not info.Exists then
                Error "That file is gone."
            elif info.Length > MaxSourceBytes then
                Error "That picture is over 25 MB."
            else
                use original = SKBitmap.Decode(path)
                toUpload original
        with ex ->
            Error("The picture could not be read: " + ex.Message)

    /// A picture that arrived as bytes - what Ctrl+V hands over.
    let fromBytes (bytes: byte[]) : Result<Encoded, string> =
        try
            if isNull bytes || bytes.Length = 0 then
                Error "There is no picture on the clipboard."
            elif int64 bytes.Length > MaxSourceBytes then
                Error "That picture is over 25 MB."
            else
                use original = SKBitmap.Decode(bytes)
                toUpload original
        with ex ->
            Error("The picture could not be read: " + ex.Message)

    /// A chat picture re-encoded as PNG, for "Save" when the user picks .png.
    /// Returns the input unchanged if it cannot be decoded.
    let toPng (webp: byte[]) : byte[] =
        try
            use bitmap = SKBitmap.Decode(webp)

            if isNull bitmap then
                webp
            else
                use image = SKImage.FromBitmap(bitmap)
                use data = image.Encode(SKEncodedImageFormat.Png, 100)
                if isNull data then webp else data.ToArray()
        with _ ->
            webp

    // -----------------------------------------------------------------------
    // LOCAL CACHE
    //
    // A chat image never changes once uploaded - its key is its identity - so a
    // copy on disk is good forever. Scrolling back through PULSE, or opening the
    // app tomorrow, reads from here and never asks the server again.
    // -----------------------------------------------------------------------
    let cacheDir =
        lazy
            (let dir =
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DLSS5Manager",
                    "Cache",
                    "Pulse"
                )

             try
                 Directory.CreateDirectory(dir) |> ignore
             with _ ->
                 ()

             dir)

    /// Keys come from the server, so they are reduced to a safe file name
    /// rather than trusted as one.
    let private fileFor (key: string) =
        use sha = SHA256.Create()

        let name =
            sha.ComputeHash(Encoding.UTF8.GetBytes(key))
            |> Array.take 12
            |> Array.map (fun b -> b.ToString("x2"))
            |> String.concat ""

        Path.Combine(cacheDir.Value, name + ".webp")

    let tryReadCached (key: string) : byte[] option =
        try
            let file = fileFor key
            if File.Exists(file) then Some(File.ReadAllBytes(file)) else None
        with _ ->
            None

    let writeCached (key: string) (bytes: byte[]) =
        try
            File.WriteAllBytes(fileFor key, bytes)
        with _ ->
            ()

    /// The disk copy is capped so a busy chat does not slowly fill the drive.
    /// Oldest files go first; called once when PULSE opens.
    let trimCache (maxBytes: int64) =
        try
            let files =
                DirectoryInfo(cacheDir.Value).GetFiles("*.webp")
                |> Array.sortBy (fun f -> f.LastAccessTimeUtc)

            let mutable total = files |> Array.sumBy (fun f -> f.Length)

            for f in files do
                if total > maxBytes then
                    total <- total - f.Length

                    try
                        f.Delete()
                    with _ ->
                        ()
        with _ ->
            ()
