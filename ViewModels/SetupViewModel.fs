namespace DLSS_5_MANAGER.ViewModels

open System
open Avalonia.Media
open Avalonia.Threading
open DLSS_5_MANAGER.Services

/// One row on the first-launch screen: a bundle, what it is doing, and how far
/// along it is.
type SetupItemViewModel(bundle: CloudAssets.Bundle) =
    inherit ViewModelBase()

    let mutable isBusy = false
    let mutable isReady = CloudAssets.isPresent bundle
    let mutable status = if CloudAssets.isPresent bundle then "Already here" else "Not downloaded yet"
    let mutable progress = if CloudAssets.isPresent bundle then 100.0 else 0.0
    let mutable hasProgress = false
    let mutable failed = false

    member _.Bundle = bundle
    member _.Title = bundle.Title
    member _.Blurb = bundle.Blurb

    member this.IsBusy
        with get () = isBusy
        and set (value) =
            if this.SetProperty(&isBusy, value) then
                this.RaisePropertyChanged("CanDownload")
                this.RaisePropertyChanged("ButtonText")
                this.RaisePropertyChanged("IsIndeterminate")

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

    member this.StatusText
        with get () = status
        and set (value) = this.SetProperty(&status, value) |> ignore

    /// 0 to 100. `HasProgress` is false while the size is unknown, which the
    /// view shows as an indeterminate bar rather than a number it made up.
    member this.Progress
        with get () = progress
        and set (value) = this.SetProperty(&progress, value) |> ignore

    member this.HasProgress
        with get () = hasProgress
        and set (value) =
            if this.SetProperty(&hasProgress, value) then
                this.RaisePropertyChanged("IsIndeterminate")

    /// The bar only sweeps while something is actually being downloaded and
    /// its size is not known yet. At rest it sits still, because a bar that
    /// moves before anything has been asked for reads as work happening.
    member _.IsIndeterminate = isBusy && not hasProgress

    member _.CanDownload = not isBusy && not isReady
    member _.ShowButton = not isReady

    member _.ButtonText =
        if isReady then "READY"
        elif isBusy then "DOWNLOADING..."
        elif failed then "TRY AGAIN"
        else "DOWNLOAD"

    /// Green once it is here, red when the last try failed, quiet otherwise.
    member _.StatusBrush: IBrush =
        let colour =
            if isReady then "#86EFAC"
            elif failed then "#F87171"
            else "#94A3B8"

        SolidColorBrush(Color.Parse(colour)) :> IBrush


/// The screen the program shows before it opens, when something it needs is not
/// on this machine yet.
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
                if args.PropertyName = "IsReady" then
                    this.RaisePropertyChanged("AllReady")
                    this.RaisePropertyChanged("CanContinue")
                    this.RaisePropertyChanged("HeadlineText"))

    member _.Items = items

    member _.AllReady = items |> Array.forall (fun i -> i.IsReady)
    member this.CanContinue = this.AllReady && not (items |> Array.exists (fun i -> i.IsBusy))

    member this.HeadlineText =
        if this.AllReady then "Everything is here" else "A few files first"

    member this.Notice
        with get () = notice
        and set (value) = this.SetProperty(&notice, value) |> ignore

    /// Fetches one bundle. Everything the view binds to is touched on the UI
    /// thread; the download itself is not.
    member this.Download(item: SetupItemViewModel) =
        if item.CanDownload then
            item.IsBusy <- true
            item.Failed <- false
            item.HasProgress <- false
            item.Progress <- 0.0
            item.StatusText <- "Starting..."
            this.Notice <- ""
            this.RaisePropertyChanged("CanContinue")

            async {
                let report (got: int64) (total: int64) =
                    Dispatcher.UIThread.Post(fun () ->
                        if total > 0L then
                            item.HasProgress <- true
                            item.Progress <- float got / float total * 100.0

                            item.StatusText <-
                                sprintf "%.1f MB of %.1f MB" (float got / 1048576.0) (float total / 1048576.0)
                        else
                            item.HasProgress <- false
                            item.StatusText <- sprintf "%.1f MB" (float got / 1048576.0))

                let! outcome = CloudAssets.fetch item.Bundle report

                Dispatcher.UIThread.Post(fun () ->
                    item.IsBusy <- false

                    match outcome with
                    | Ok() ->
                        // Believed only because the files are on disk, not
                        // because the download said so.
                        if CloudAssets.isPresent item.Bundle then
                            item.IsReady <- true
                            item.Progress <- 100.0
                            item.HasProgress <- true
                            item.StatusText <- "Ready"
                        else
                            item.Failed <- true
                            item.StatusText <- "The files did not end up where they belong."
                    | Result.Error message ->
                        item.Failed <- true
                        item.StatusText <- "Could not download."
                        this.Notice <- message

                    this.RaisePropertyChanged("CanContinue"))
            }
            |> Async.Start

    /// The one button that does both, for the person who just wants to get on
    /// with it.
    member this.DownloadEverything() =
        for item in items do
            this.Download(item)
