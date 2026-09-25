namespace DLSS_5_MANAGER.ViewModels

open System
open Avalonia.Media
open Avalonia.Threading
open DLSS_5_MANAGER.Services

/// One card in the Downloads sheet: a bundle, what it is doing, and how far
/// along it is.
type SetupItemViewModel(bundle: CloudAssets.Bundle) =
    inherit ViewModelBase()

    let mutable isBusy = false
    let mutable isReady = CloudAssets.isPresent bundle

    /// Held as a function of the language rather than as text, so a language
    /// change redraws the line without forgetting what it was saying.
    let mutable status: Localization.Strings -> string =
        if isReady then (fun l -> l.AssetReady) else (fun l -> l.AssetNotDownloaded)

    let mutable progress = if isReady then 100.0 else 0.0
    let mutable hasProgress = false
    let mutable failed = false

    member _.Bundle = bundle
    member _.Title = bundle.Title

    /// Picks the card's icon in the sheet.
    member _.IsOverlay = bundle.Id = "overlay"

    member this.IsBusy
        with get () = isBusy
        and set (value) =
            if this.SetProperty(&isBusy, value) then
                this.RaisePropertyChanged("CanDownload")
                this.RaisePropertyChanged("ButtonText")
                this.RaisePropertyChanged("IsIndeterminate")
                this.RaisePropertyChanged("ShowPercent")

    member this.IsReady
        with get () = isReady
        and set (value) =
            if this.SetProperty(&isReady, value) then
                this.RaisePropertyChanged("CanDownload")
                this.RaisePropertyChanged("ButtonText")
                this.RaisePropertyChanged("ShowButton")
                this.RaisePropertyChanged("StatusBrush")

    member this.Failed
        with get () = failed
        and set (value) =
            if this.SetProperty(&failed, value) then
                this.RaisePropertyChanged("StatusBrush")
                this.RaisePropertyChanged("ButtonText")

    member _.StatusText = status Localization.current

    member this.SetStatus(describe: Localization.Strings -> string) =
        status <- describe
        this.RaisePropertyChanged("StatusText")

    /// 0 to 100. `HasProgress` is false while the size is unknown, which the
    /// view shows as an indeterminate bar rather than a number it made up.
    member this.Progress
        with get () = progress
        and set (value) =
            if this.SetProperty(&progress, value) then
                this.RaisePropertyChanged("PercentText")

    member this.HasProgress
        with get () = hasProgress
        and set (value) =
            if this.SetProperty(&hasProgress, value) then
                this.RaisePropertyChanged("IsIndeterminate")
                this.RaisePropertyChanged("ShowPercent")

    member _.PercentText = sprintf "%.0f%%" progress
    member _.ShowPercent = isBusy && hasProgress

    /// The bar only sweeps while something is actually being downloaded and
    /// its size is not known yet. At rest it sits still, because a bar that
    /// moves before anything has been asked for reads as work happening.
    member _.IsIndeterminate = isBusy && not hasProgress

    member _.CanDownload = not isBusy && not isReady
    member _.ShowButton = not isReady

    member _.ButtonText =
        let l = Localization.current

        if isBusy then l.AssetDownloading
        elif failed then l.AssetRetry
        else l.AssetDownload

    /// Green once it is here, red when the last try failed, quiet otherwise.
    member _.StatusBrush: IBrush =
        let colour =
            if isReady then "#86EFAC"
            elif failed then "#F87171"
            else "#FCD34D"

        SolidColorBrush(Color.Parse(colour)) :> IBrush

    /// Everything that reads the language, for a language change.
    member this.Relabel() =
        this.RaisePropertyChanged("StatusText")
        this.RaisePropertyChanged("ButtonText")


/// The Downloads sheet: the parts of the payload fetched from our own storage
/// (`CloudAssets`). Offered, never required - the program opens and installs
/// without them, and the download button at the top brings this back.
type SetupViewModel() as this =
    inherit ViewModelBase()

    let items =
        CloudAssets.bundles ()
        |> List.map SetupItemViewModel
        |> Array.ofList

    let mutable notice = ""

    do
        for item in items do
            item.PropertyChanged.Add(fun args ->
                match args.PropertyName with
                | "IsReady" ->
                    this.RaisePropertyChanged("AllReady")
                    this.RaisePropertyChanged("HasMissing")
                    this.RaisePropertyChanged("CanDownloadAll")
                    this.RaisePropertyChanged("HeadlineText")
                    this.RaisePropertyChanged("IsOverlayReady")
                | "IsBusy" ->
                    this.RaisePropertyChanged("IsAnyBusy")
                    this.RaisePropertyChanged("CanDownloadAll")
                | _ -> ())

    member _.Items = items

    member _.AllReady = items |> Array.forall (fun i -> i.IsReady)
    member this.HasMissing = not this.AllReady
    member _.IsAnyBusy = items |> Array.exists (fun i -> i.IsBusy)
    member this.CanDownloadAll = not this.AllReady && not this.IsAnyBusy

    /// The overlay is the one bundle the Manage sheet cares about: its option
    /// there stays locked until the add-on is on disk.
    member _.IsOverlayReady =
        items
        |> Array.exists (fun i -> i.IsOverlay && i.IsReady)

    member this.HeadlineText =
        if this.AllReady then Localization.current.AssetsAllReady else Localization.current.AssetsTitle

    member this.Notice
        with get () = notice
        and set (value) = this.SetProperty(&notice, value) |> ignore

    member this.Relabel() =
        for item in items do
            item.Relabel()

        this.RaisePropertyChanged("HeadlineText")

    /// Fetches one bundle. Everything the view binds to is touched on the UI
    /// thread; the download itself is not.
    member this.Download(item: SetupItemViewModel) =
        if item.CanDownload then
            item.IsBusy <- true
            item.Failed <- false
            item.HasProgress <- false
            item.Progress <- 0.0
            item.SetStatus(fun l -> l.AssetStarting)
            this.Notice <- ""

            async {
                let report (got: int64) (total: int64) =
                    Dispatcher.UIThread.Post(fun () ->
                        if total > 0L then
                            item.HasProgress <- true
                            item.Progress <- float got / float total * 100.0

                            let text =
                                sprintf "%.1f MB / %.1f MB" (float got / 1048576.0) (float total / 1048576.0)

                            item.SetStatus(fun _ -> text)
                        else
                            item.HasProgress <- false
                            let text = sprintf "%.1f MB" (float got / 1048576.0)
                            item.SetStatus(fun _ -> text))

                let! outcome = CloudAssets.fetch item.Bundle report

                Dispatcher.UIThread.Post(fun () ->
                    item.IsBusy <- false

                    match outcome with
                    | Ok() ->
                        // Believed only because the files are on disk, not
                        // because the download said so.
                        if CloudAssets.isPresent item.Bundle then
                            item.Progress <- 100.0
                            item.HasProgress <- true
                            item.SetStatus(fun l -> l.AssetReady)
                            item.IsReady <- true
                        else
                            item.Failed <- true
                            item.SetStatus(fun l -> l.AssetMisplaced)
                    | Result.Error message ->
                        item.Failed <- true
                        item.SetStatus(fun l -> l.AssetFailed)
                        this.Notice <- message)
            }
            |> Async.Start

    /// The one button that does all of them, for the person who just wants to
    /// get on with it.
    member this.DownloadEverything() =
        for item in items do
            this.Download(item)
