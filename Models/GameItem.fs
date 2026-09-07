namespace DLSS_5_MANAGER.Models

open System

[<CLIMutable>]
type GameItem = {
    AppId: string
    Title: string
    LauncherTypeName: string
    InstallDirectory: string
    TargetExecutablePath: string
    TargetExecutableSize: int64
    LocalBannerPath: string
    LastManifestTimestamp: int64
    UpscaleStatus: string
    DlssVersion: string
    FsrVersion: string
    XessVersion: string
}
