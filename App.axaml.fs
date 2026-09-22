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
            // CloudAssets. When any of it is missing, that screen comes first
            // and nothing of the program is built until it is done: no view
            // model, no library scan, no way past it.
            if CloudAssets.allPresent () then
                this.OpenMain(desktop)
            else
                // Closing the setup window must not be read as closing the
                // program, because the real window has not been made yet.
                desktop.ShutdownMode <- ShutdownMode.OnExplicitShutdown

                let setup = SetupWindow(DataContext = SetupViewModel())

                setup.Finished.Add(fun () ->
                    this.OpenMain(desktop)
                    desktop.ShutdownMode <- ShutdownMode.OnMainWindowClose
                    setup.Close())

                setup.Closed.Add(fun _ ->
                    // Given up on rather than finished: there is nothing to
                    // run without those files.
                    if not setup.Completed then desktop.Shutdown(0))

                desktop.MainWindow <- setup
                setup.Show()
        | _ -> ()

        base.OnFrameworkInitializationCompleted()
