namespace DLSS_5_MANAGER.Views

open Avalonia.Controls
open Avalonia.Input
open Avalonia.Interactivity
open Avalonia.Markup.Xaml
open DLSS_5_MANAGER.ViewModels

/// The screen the program shows before it opens, when something it needs has
/// not been downloaded to this machine yet.
///
/// It is a window of its own rather than a panel inside the main one on
/// purpose: nothing of the program proper is built until this is done with, so
/// there is no half-started library scan behind it and no way past it.
type SetupWindow() as this =
    inherit Window()

    /// Raised once everything is in place and the person has pressed Continue.
    let finished = Event<unit>()

    /// True when the window was closed by pressing Continue rather than by
    /// giving up, which is the difference between opening the program and
    /// ending it.
    ///
    /// A plain mutable and not `member val`: an auto-property is initialised
    /// AFTER the `do` below, which makes F# guard every use of `this` in there
    /// with a check that then fails at startup. This class is constructed
    /// before anything else exists, so there is nothing to see that from.
    let mutable completed = false

    do this.InitializeComponent()

    member private this.InitializeComponent() = AvaloniaXamlLoader.Load(this)

    /// Subscribed to by App, which opens the program proper when it fires.
    [<CLIEvent>]
    member _.Finished = finished.Publish

    member _.Completed
        with get () = completed
        and set (value) = completed <- value

    /// No title bar, so the whole face of it drags.
    member this.OnDragArea(sender: obj, e: PointerPressedEventArgs) =
        if e.GetCurrentPoint(this).Properties.IsLeftButtonPressed then
            this.BeginMoveDrag(e)

    member this.OnDownloadClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext, sender with
        | (:? SetupViewModel as vm), (:? Button as button) ->
            match button.DataContext with
            | :? SetupItemViewModel as item -> vm.Download(item)
            | _ -> ()
        | _ -> ()

    member this.OnDownloadAllClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? SetupViewModel as vm -> vm.DownloadEverything()
        | _ -> ()

    member this.OnContinueClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? SetupViewModel as vm when vm.CanContinue ->
            this.Completed <- true
            finished.Trigger()
        | _ -> ()

    member this.OnCloseClicked(sender: obj, e: RoutedEventArgs) = this.Close()
