namespace DLSS_5_MANAGER.Services

open System
open System.IO
open System.Text.Json

/// The window's background: one picture per colour atmosphere, or one the user
/// picked, shown dimmed.
///
/// Kept in its own `background.json` rather than in `AppSettings`: a field
/// there means touching every `saveSettings` call site, and none of those
/// places has anything to do with the background.
module BackgroundStore =

    [<CLIMutable>]
    type BackgroundSettings =
        { /// The user's own picture instead of the atmosphere's.
          UseCustom: bool
          /// Our copy of it, under `Backgrounds\` - never the file they picked.
          CustomFile: string
          /// Its mean luminance (0-255), measured once when it was picked,
          /// because measuring a large photo on every launch would cost a
          /// visible moment. Zero means not measured yet.
          CustomLuminance: float
          /// The mean luminance of its brightest quarter - what the glare is
          /// judged on (see `glare`). Zero: not measured yet.
          CustomBright: float
          /// 15 to 100. Zero is what a file without the field reads back as,
          /// and means the default.
          Brightness: int }

    /// Low on purpose: these are bright, saturated pictures behind white text,
    /// and the app is often open in a dark room next to a game.
    [<Literal>]
    let DefaultBrightness = 25

    [<Literal>]
    let MinBrightness = 15

    [<Literal>]
    let MaxBrightness = 100

    let private rootFolder () =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DLSS5Manager")

    let private settingsPath () = Path.Combine(rootFolder (), "background.json")

    let private customFolder () = Path.Combine(rootFolder (), "Backgrounds")

    let private clamp (value: int) =
        if value <= 0 then DefaultBrightness
        else max MinBrightness (min MaxBrightness value)

    let private defaults () =
        { UseCustom = false
          CustomFile = ""
          CustomLuminance = 0.0
          CustomBright = 0.0
          Brightness = DefaultBrightness }

    let load () : BackgroundSettings =
        try
            let path = settingsPath ()

            if File.Exists(path) then
                let options = JsonSerializerOptions()
                options.PropertyNameCaseInsensitive <- true
                let s = JsonSerializer.Deserialize<BackgroundSettings>(File.ReadAllText(path), options)

                let file = if isNull s.CustomFile then "" else s.CustomFile

                { UseCustom = s.UseCustom && File.Exists(file)
                  CustomFile = file
                  CustomLuminance = s.CustomLuminance
                  CustomBright = s.CustomBright
                  Brightness = clamp s.Brightness }
            else
                defaults ()
        with _ ->
            defaults ()

    let save (settings: BackgroundSettings) =
        try
            Directory.CreateDirectory(rootFolder ()) |> ignore
            let options = JsonSerializerOptions()
            options.WriteIndented <- true
            File.WriteAllText(settingsPath (), JsonSerializer.Serialize(settings, options))
        with _ ->
            ()

    /// "Neon Emerald" -> "neon-emerald", the name the picture ships under.
    let private slug (atmosphereKey: string) =
        atmosphereKey.Trim().ToLowerInvariant().Replace(' ', '-')

    /// The atmosphere's picture, shipped beside the executable (`backgrounds\`).
    let themeImage (atmosphereKey: string) =
        Path.Combine(AppContext.BaseDirectory, "backgrounds", slug atmosphereKey + ".jpg")

    /// A 320 px copy of the same picture, for the picker in Settings.
    let themeThumbnail (atmosphereKey: string) =
        Path.Combine(AppContext.BaseDirectory, "backgrounds", "thumbs", slug atmosphereKey + ".jpg")

    /// The mean luminance of a dark picture that looks right at the default
    /// brightness (Neon Emerald measures 40, Cyber Nebula 54). Anything
    /// brighter is dimmed further - see `exposure`.
    [<Literal>]
    let ReferenceLuminance = 55.0

    /// A picture's mean luminance, 0-255, from a 32 x 18 copy of it. Read
    /// with System.Drawing, which the app already ships, so no pixel access
    /// is needed from Avalonia. The reference value when it cannot be read.
    /// The mean luminance and the mean of the brightest quarter of the same
    /// 32 x 18 copy. A dark picture with one bright thing in it - Deep Astral's
    /// galaxy, Cyber Nebula's clouds - has a low mean but washes out whatever
    /// text lies over the bright part; the glare is judged on that part.
    let measureLights (path: string) : float * float =
        try
            use source = System.Drawing.Image.FromFile(path)
            use small = new System.Drawing.Bitmap(32, 18)

            let shrink () =
                use g = System.Drawing.Graphics.FromImage(small)
                g.InterpolationMode <- System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear
                g.DrawImage(source, 0, 0, 32, 18)

            shrink ()

            let cells =
                [| for y in 0..17 do
                       for x in 0..31 do
                           let c = small.GetPixel(x, y)
                           yield 0.2126 * float c.R + 0.7152 * float c.G + 0.0722 * float c.B |]

            let sorted = Array.sort cells
            Array.average cells, Array.average sorted.[sorted.Length - sorted.Length / 4 ..]
        with _ ->
            ReferenceLuminance, ReferenceLuminance

    let measureLuminance (path: string) : float =
        try
            use source = System.Drawing.Image.FromFile(path)
            use small = new System.Drawing.Bitmap(32, 18)

            // Its own scope: the Graphics has to be released before the
            // pixels are read back.
            let shrink () =
                use g = System.Drawing.Graphics.FromImage(small)
                g.InterpolationMode <- System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear
                g.DrawImage(source, 0, 0, 32, 18)

            shrink ()
            let mutable sum = 0.0

            for y in 0..17 do
                for x in 0..31 do
                    let c = small.GetPixel(x, y)
                    sum <- sum + 0.2126 * float c.R + 0.7152 * float c.G + 0.0722 * float c.B

            sum / (32.0 * 18.0)
        with _ ->
            ReferenceLuminance

    /// How much of the picture comes through the black layer, 0 to 1.
    ///
    /// The slider alone is not enough: Frost Glacier is four times as bright
    /// as Neon Emerald, so the same setting that makes one comfortable leaves
    /// the other glaring behind white text. A bright picture is scaled down
    /// towards the reference first. The cubic term hands that correction
    /// back as the slider nears 100, so 100% is still the picture as it is.
    let exposure (brightness: int) (luminance: float) =
        let b = float (clamp brightness) / 100.0

        let m =
            if luminance <= ReferenceLuminance then 1.0 else ReferenceLuminance / luminance

        b * m + (1.0 - m) * b * b * b

    /// How much the picture on screen washes out white text laid over it, 0
    /// to 1: its mean luminance after the dim layer, from where the look stops
    /// being comfortable (48) to a bright photo shown as it is (130). The
    /// glass surfaces darken and text on the picture gets a plate as it rises
    /// (MainViewModel's glare brushes); over a dark picture it stays at 0 and
    /// nothing changes.
    ///
    /// Judged on the brightest quarter of the picture (`bright`), dimmed as the
    /// whole picture is (`exposure` works from the mean): judged on the mean,
    /// Deep Astral (mean 41, its galaxy 89) never counted as bright at any
    /// setting while the cards over its galaxy washed out. From 40 (no glare)
    /// to 110 (full), at 100%: Obsidian Onyx .15, Neon Emerald .6, Deep Astral
    /// .78, Cyber Nebula, Frost Glacier, Emerald Horizon and Supernova Flare in
    /// full. At the default 25% every dark picture stays at 0.
    ///
    /// The slider counts on its own as well, whatever the picture: raising it
    /// from the default 25% to 100% brings the glare in fully on every one -
    /// night or day, a theme or the user's own. Judged on the picture alone,
    /// Midnight Titanium, Obsidian Onyx and Eclipse Crimson barely moved (the
    /// user: "some of them have no effect when the brightness goes up"). The
    /// picture only decides how much sooner it comes in.
    let glare (brightness: int) (luminance: float) (bright: float) =
        let smooth (x: float) =
            let x = max 0.0 (min 1.0 x)
            x * x * (3.0 - 2.0 * x)

        let shown = bright * exposure brightness luminance
        let byPicture = smooth ((shown - 40.0) / (110.0 - 40.0))

        let bySlider =
            smooth (float (clamp brightness - DefaultBrightness) / float (MaxBrightness - DefaultBrightness))

        max byPicture bySlider

    /// Copies the picture into our own folder, so moving or deleting the
    /// original does not take the background with it. Each copy gets a new
    /// name: the one on screen stays readable while the new one is written,
    /// and the older copies are cleared out afterwards.
    let importCustom (source: string) : string option =
        try
            let folder = customFolder ()
            Directory.CreateDirectory(folder) |> ignore

            let ext =
                match Path.GetExtension(source) with
                | null
                | "" -> ".png"
                | e -> e.ToLowerInvariant()

            let target = Path.Combine(folder, sprintf "custom_%d%s" DateTime.UtcNow.Ticks ext)
            File.Copy(source, target, true)

            for old in Directory.GetFiles(folder, "custom_*") do
                if not (String.Equals(old, target, StringComparison.OrdinalIgnoreCase)) then
                    try
                        File.Delete(old)
                    with _ ->
                        ()

            Some target
        with _ ->
            None

    /// Removes our copy. The picture the user picked was never ours to touch.
    let removeCustom () =
        try
            let folder = customFolder ()

            if Directory.Exists(folder) then
                for old in Directory.GetFiles(folder, "custom_*") do
                    try
                        File.Delete(old)
                    with _ ->
                        ()
        with _ ->
            ()
