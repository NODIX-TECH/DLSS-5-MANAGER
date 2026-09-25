namespace DLSS_5_MANAGER

open Avalonia
open Avalonia.Controls
open Avalonia.Controls.ApplicationLifetimes
open Avalonia.Markup.Xaml
open DLSS_5_MANAGER.Services
open DLSS_5_MANAGER.ViewModels
open DLSS_5_MANAGER.Views

type App() =
    inherit Application()

    override this.Initialize() =
        AvaloniaXamlLoader.Load(this)

    /// Builds the program proper and shows it.
    member private this.OpenMain(desktop: IClassicDesktopStyleApplicationLifetime) =
        let window = MainWindow(DataContext = MainViewModel())
        desktop.MainWindow <- window

        if System.Environment.GetCommandLineArgs() |> Array.contains "--screen" then
            let vm = window.DataContext :?> MainViewModel
            vm.ActiveSection <- "screen"
            window.Opened.Add(fun _ -> vm.Screen.Start())

        window.Show()

    override this.OnFrameworkInitializationCompleted() =
        match this.ApplicationLifetime with
        | :? IClassicDesktopStyleApplicationLifetime as desktop ->
            // Some of the payload is fetched rather than installed - see
            // CloudAssets. It no longer stands in front of the program: the
            // Downloads sheet inside the main window offers whatever is
            // missing, and the program works without it meanwhile.
            this.OpenMain(desktop)
        | _ -> ()

        base.OnFrameworkInitializationCompleted()
