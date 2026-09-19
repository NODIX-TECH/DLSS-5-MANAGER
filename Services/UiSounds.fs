namespace DLSS_5_MANAGER.Services

open System
open System.IO
open System.Runtime.InteropServices

/// Calm interface sounds, synthesised in memory and played by Windows itself.
///
/// No sound files ship and no audio package is loaded: each sound is a few
/// soft notes rendered once, the first time it is needed, into a WAV buffer
/// that stays pinned for PlaySound to read. Every call is asynchronous, so a
/// sound never holds up the UI.
///
/// **The voice.** These sit in the 260-520 Hz range - the part of the scale a
/// speaking voice lives in, which the ear reads as warm rather than as an
/// alert. Every note also carries the octave *below* it at nearly equal
/// strength, and that lower partial is what gives the sound its body; only a
/// trace of the octave above is left in, for air. The older sounds were a
/// single 1175 Hz sine, which is up where a smoke alarm sits - thin, and
/// tiring after the tenth click.
module UiSounds =

    [<DllImport("winmm.dll", EntryPoint = "PlaySoundW")>]
    extern bool private PlaySound(nativeint sound, nativeint hmod, uint32 flags)

    [<Literal>]
    let private Rate = 22050

    /// Notes are (frequency Hz, length ms, loudness 0..1), played one after
    /// another. Each has a gentle fade in and out, so nothing clicks, and decays
    /// like something struck rather than stopping dead.
    let private render (notes: (float * int * float) list) =
        let samples =
            notes
            |> List.map (fun (freq, millis, amp) ->
                let n = Rate * millis / 1000

                Array.init n (fun i ->
                    let t = float i / float Rate

                    // Longer than before at both ends: a slow edge is most of
                    // what separates a soft sound from a blip.
                    let attack = min 1.0 (float i / (float Rate * 0.014))
                    let release = min 1.0 (float (n - i) / (float Rate * 0.035))

                    // sin(pi*f*t) is sin(2*pi*(f/2)*t) - the octave below, and
                    // the reason any of this has weight to it.
                    let tone =
                        0.60 * sin (2.0 * Math.PI * freq * t)
                        + 0.52 * sin (Math.PI * freq * t)
                        + 0.05 * sin (4.0 * Math.PI * freq * t)

                    // Was exp(-t*6): far too quick, which read as a click.
                    amp * attack * release * exp (-t * 3.1) * tone))
            |> Array.concat

        let dataBytes = samples.Length * 2

        use ms = new MemoryStream()
        use w = new BinaryWriter(ms)
        w.Write("RIFF"B)
        w.Write(36 + dataBytes)
        w.Write("WAVE"B)
        w.Write("fmt "B)
        w.Write(16)
        w.Write(1s)
        w.Write(1s)
        w.Write(Rate)
        w.Write(Rate * 2)
        w.Write(2s)
        w.Write(16s)
        w.Write("data"B)
        w.Write(dataBytes)

        for s in samples do
            w.Write(int16 (Math.Clamp(s, -1.0, 1.0) * 32767.0))

        w.Flush()
        GCHandle.Alloc(ms.ToArray(), GCHandleType.Pinned)

    let private play (sound: Lazy<GCHandle>) =
        try
            // SND_ASYNC | SND_NODEFAULT | SND_MEMORY
            PlaySound(sound.Value.AddrOfPinnedObject(), 0n, 0x0001u ||| 0x0002u ||| 0x0004u) |> ignore
        with _ ->
            ()

    // G4 with G3 underneath it: low enough to feel soft, high enough to stay
    // audible over a game running in the background.
    let private tickSound = lazy (render [ 392.00, 75, 0.11 ])

    /// Turning something off answers a third lower, so the two read as a pair
    /// without either of them being a different kind of sound.
    let private tickOffSound = lazy (render [ 311.13, 80, 0.11 ])

    /// C major, climbing. Warm because it starts at middle C rather than an
    /// octave above it, where the old one began.
    let private riseSound =
        lazy (render [ 261.63, 130, 0.15; 329.63, 130, 0.15; 392.00, 330, 0.14 ])

    /// The install notes, backwards.
    let private fallSound =
        lazy (render [ 392.00, 130, 0.14; 329.63, 130, 0.15; 261.63, 330, 0.15 ])

    /// Four notes up the same chord - brighter than the rest, but it still
    /// stops short of where the old sounds lived.
    let private sparkleSound =
        lazy (render [ 392.00, 85, 0.13; 523.25, 85, 0.13; 659.25, 95, 0.12; 783.99, 270, 0.09 ])

    /// Any choice in the Manage sheet that was not already the one selected:
    /// the route, the API, the build, or an add-on being switched on.
    let tick () = play tickSound

    /// An add-on being switched off.
    let tickOff () = play tickOffSound

    /// Switched on or off, whichever way it went.
    let toggle (on: bool) = if on then tick () else tickOff ()

    /// A mod install or route switch finished.
    let installDone () = play riseSound

    /// A mod removal finished - the install chime, falling.
    let uninstallDone () = play fallSound

    /// A result was shared with the community.
    let published () = play sparkleSound
