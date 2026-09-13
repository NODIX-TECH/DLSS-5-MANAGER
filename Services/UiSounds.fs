namespace DLSS_5_MANAGER.Services

open System
open System.IO
open System.Runtime.InteropServices

/// Calm interface sounds, synthesised in memory and played by Windows itself.
///
/// No sound files ship and no audio package is loaded: each sound is a few
/// soft sine notes rendered once, the first time it is needed, into a WAV
/// buffer that stays pinned for PlaySound to read. Every call is asynchronous,
/// so a sound never holds up the UI.
module UiSounds =

    [<DllImport("winmm.dll", EntryPoint = "PlaySoundW")>]
    extern bool private PlaySound(nativeint sound, nativeint hmod, uint32 flags)

    [<Literal>]
    let private Rate = 22050

    /// Notes are (frequency Hz, length ms, loudness 0..1), played one after
    /// another. Each has a short fade in and out, so nothing clicks, and a
    /// touch of its octave, so it sounds like a soft bell rather than a beep.
    let private render (notes: (float * int * float) list) =
        let samples =
            notes
            |> List.map (fun (freq, millis, amp) ->
                let n = Rate * millis / 1000

                Array.init n (fun i ->
                    let t = float i / float Rate
                    let attack = min 1.0 (float i / (float Rate * 0.008))
                    let release = min 1.0 (float (n - i) / (float Rate * 0.015))
                    let tone = sin (2.0 * Math.PI * freq * t) + 0.18 * sin (4.0 * Math.PI * freq * t)
                    amp * attack * release * exp (-t * 6.0) * tone))
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

    let private tickSound = lazy (render [ 1174.7, 55, 0.07 ])

    let private riseSound =
        lazy (render [ 659.25, 110, 0.13; 880.0, 110, 0.13; 1318.5, 280, 0.12 ])

    /// The install notes, backwards.
    let private fallSound =
        lazy (render [ 1318.5, 110, 0.12; 880.0, 110, 0.13; 659.25, 280, 0.13 ])

    let private sparkleSound =
        lazy (render [ 784.0, 80, 0.11; 1046.5, 80, 0.11; 1568.0, 90, 0.10; 2093.0, 240, 0.07 ])

    /// A switch between install routes.
    let tick () = play tickSound

    /// A mod install or route switch finished.
    let installDone () = play riseSound

    /// A mod removal finished - the install chime, falling.
    let uninstallDone () = play fallSound

    /// A result was shared with the community.
    let published () = play sparkleSound
