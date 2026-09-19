namespace DLSS_5_MANAGER

open Avalonia
open Avalonia.Controls.ApplicationLifetimes
open Avalonia.Markup.Xaml
open DLSS_5_MANAGER.ViewModels
open DLSS_5_MANAGER.Views

type App() =
    inherit Application()

    override this.Initialize() =
        AvaloniaXamlLoader.Load(this)

    override this.OnFrameworkInitializationCompleted() =
        match this.ApplicationLifetime with
        | :? IClassicDesktopStyleApplicationLifetime as desktop ->
            desktop.MainWindow <- MainWindow(DataContext = MainViewModel())
            if System.Environment.GetCommandLineArgs() |> Array.contains "--screen" then
                let vm = desktop.MainWindow.DataContext :?> MainViewModel
                vm.ActiveSection <- "screen"
                desktop.MainWindow.Opened.Add(fun _ -> vm.Screen.Start())
        | _ -> ()

        base.OnFrameworkInitializationCompleted()
