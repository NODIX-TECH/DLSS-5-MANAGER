namespace DLSS_5_MANAGER.ViewModels

open System
open System.IO
open System.Text.RegularExpressions
open Avalonia.Media.Imaging
open DLSS_5_MANAGER.Models
open DLSS_5_MANAGER.Services

type GameCardViewModel(game: GameItem) =
    inherit ViewModelBase()

    /// The record is swapped whenever the user corrects the detected executable.
    let mutable currentGame = game

    let mutable bannerBitmap : Bitmap option = None
    let mutable isBitmapLoaded = false
    let mutable isVerticalCover = false
    let mutable isDragging = false
    let mutable isDragTarget = false

    let cleanDisplayTitle (raw: string) =
        let cleaned = Regex.Replace(raw, @"\[.*?\]|\(.*?\)", "").Trim()
        if String.IsNullOrWhiteSpace(cleaned) then raw else cleaned

    let loadBitmap () =
        if not isBitmapLoaded then
            isBitmapLoaded <- true
            try
                if not (String.IsNullOrWhiteSpace(currentGame.LocalBannerPath)) && File.Exists(currentGame.LocalBannerPath) then
                    use stream = File.OpenRead(currentGame.LocalBannerPath)
                    // Decoding at card width keeps an extracted icon as sharp as
                    // its source allows instead of scaling a thumbnail up later.
                    let bmp = Bitmap.DecodeToWidth(stream, 432)
                    isVerticalCover <- (bmp.Size.Height > bmp.Size.Width && bmp.Size.Width >= 120.0)
                    bannerBitmap <- Some bmp
            with _ ->
                bannerBitmap <- None

    /// Reads the install manifest once and turns it into the short badge the
    /// card wears. Empty when this app did not install anything on the game.
    let readBadge () =
        try
            let (route, arch) = ModInstaller.installedRouteAndArch currentGame

            match route with
            | "optiscaler" ->
                if ModInstaller.installedOptiApi currentGame = "vulkan" then "OPTI-VK" else "OPTI"
            | "dx12" -> "DX12"
            | "dx11" -> if arch = "32" then "DX11-32" else "DX11"
            | "dx9" -> if arch = "32" then "DX9-32" else "DX9-64"
            | "amd" -> "AMD"
            | "emulator" -> "EMU"
            | _ -> ""
        with _ ->
            ""

    let mutable badge = readBadge ()

    member this.Game = currentGame

    /// Replaces the auto-detected executable with a user supplied one.
    member this.SetExecutable(path: string) =
        let size =
            try FileInfo(path).Length
            with _ -> 0L

        currentGame <-
            { currentGame with
                TargetExecutablePath = path
                TargetExecutableSize = size }

        this.RaisePropertyChanged("ExecutablePath")

    /// Points the card at artwork the user picked themselves and redraws it.
    member this.SetBanner(path: string) =
        currentGame <- { currentGame with LocalBannerPath = path }

        // Force the next read to decode the new file.
        bannerBitmap <- None
        isBitmapLoaded <- false
        isVerticalCover <- false

        this.RaisePropertyChanged("BannerImage")
        this.RaisePropertyChanged("HasBannerImage")
        this.RaisePropertyChanged("IsVerticalCover")
        this.RaisePropertyChanged("IsAppIcon")


    /// Refreshed after an install or a removal, so the grid stays truthful
    /// without re-reading every manifest on every repaint.
    member this.RefreshModBadge() =
        badge <- readBadge ()
        this.RaisePropertyChanged("ModBadgeText")
        this.RaisePropertyChanged("HasModBadge")

    member this.ModBadgeText = badge
    member this.HasModBadge = badge <> ""

    member this.Title = cleanDisplayTitle currentGame.Title
    member this.RawTitle = currentGame.Title
    member this.LauncherType = currentGame.LauncherTypeName
    member this.InstallDirectory = currentGame.InstallDirectory
    member this.ExecutablePath = currentGame.TargetExecutablePath
    member this.UpscaleStatus = currentGame.UpscaleStatus

    member this.HasDlss = not (String.IsNullOrWhiteSpace(currentGame.DlssVersion))
    member this.HasFsr = not (String.IsNullOrWhiteSpace(currentGame.FsrVersion))
    member this.HasXess = not (String.IsNullOrWhiteSpace(currentGame.XessVersion))

    member this.IsLongTitle: bool = this.Title.Length > 28

    member this.MarqueeString: string =
        if this.Title.Length > 28 then
            sprintf "%s      â€¢      %s      â€¢      " this.Title this.Title
        else
            this.Title

    member this.BannerImage: Bitmap =
        loadBitmap ()
        match bannerBitmap with
        | Some bmp -> bmp
        | None -> null

    member this.HasBannerImage: bool =
        loadBitmap ()
        bannerBitmap.IsSome

    member this.IsVerticalCover: bool =
        loadBitmap ()
        isVerticalCover

    member this.IsAppIcon: bool =
        loadBitmap ()
        bannerBitmap.IsSome && not isVerticalCover

    member this.IsDragging
        with get () = isDragging
        and set value =
            if this.SetProperty(&isDragging, value) then
                this.RaisePropertyChanged("IsDragging")

    member this.IsDragTarget
        with get () = isDragTarget
        and set value =
            if this.SetProperty(&isDragTarget, value) then
                this.RaisePropertyChanged("IsDragTarget")

    member this.LauncherBadgeBackground: string =
        match currentGame.LauncherTypeName with
        | "STEAM" -> "#1E3A8A"
        | "EPIC GAMES" -> "#1E293B"
        | "GOG GALAXY" -> "#581C87"
        | "REPACK" -> "#831843"
        | _ -> "#334155"

    member this.HasUpscaleStatus: bool =
        let s = currentGame.UpscaleStatus
        not (String.IsNullOrWhiteSpace(s)) && not (s.Contains("Direct3D")) && not (s.Contains("Custom")) && not (s.Contains("Default"))

    member this.UpscaleBadgeBackground: string =
        if currentGame.UpscaleStatus.Contains("OptiScaler") then "#0891B2"
        elif currentGame.UpscaleStatus.Contains("DLSS") then "#16A34A"
        elif currentGame.UpscaleStatus.Contains("FSR") then "#EA580C"
        elif currentGame.UpscaleStatus.Contains("XeSS") then "#2563EB"
        else "#334155"

