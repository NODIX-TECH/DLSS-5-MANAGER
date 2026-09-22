// Keep Windows screenshot shortcuts working without feeding our output back
// into desktop duplication. Only these shortcuts are observed; none are consumed.
static SRWLOCK g_capture_gate = SRWLOCK_INIT;
static volatile LONG g_snapshot_frozen = 0;
static HHOOK g_snapshot_hook = nullptr;
static constexpr UINT kSnapshotTimer = 481;
struct CaptureGuard {
    CaptureGuard() { AcquireSRWLockExclusive(&g_capture_gate); }
    ~CaptureGuard() { ReleaseSRWLockExclusive(&g_capture_gate); }
};

static void EndSnapshot()
{
    CaptureGuard guard;
    if (!g_snapshot_frozen) return;
    // Restore exclusion and wait for composition BEFORE allowing capture again.
    SetWindowDisplayAffinity(g_present_hwnd, g_wgc_active || g_present_capturable ? 0 : WDA_EXCLUDEFROMCAPTURE);
    DwmFlush();
    g_force_next_frame = true;
    InterlockedExchange(&g_snapshot_frozen, 0);
    KillTimer(g_present_hwnd, kSnapshotTimer);
    Log("[snapshot] desktop capture resumed");
}

static void BeginSnapshot(UINT duration)
{
    if (!g_present_hwnd || g_wgc_active || !IsWindowVisible(g_present_hwnd)) return;
    CaptureGuard guard;
    InterlockedExchange(&g_snapshot_frozen, 1);
    if (!SetWindowDisplayAffinity(g_present_hwnd, 0)) {
        InterlockedExchange(&g_snapshot_frozen, 0);
        Log("[snapshot] capture affinity failed %lu", GetLastError());
        return;
    }
    DwmFlush();
    if (!SetTimer(g_present_hwnd, kSnapshotTimer, duration, nullptr)) {
        SetWindowDisplayAffinity(g_present_hwnd, WDA_EXCLUDEFROMCAPTURE);
        DwmFlush();
        InterlockedExchange(&g_snapshot_frozen, 0);
        return;
    }
    Log("[snapshot] processed frame available for %u ms", duration);
}

static LRESULT CALLBACK SnapshotKeys(int code, WPARAM message, LPARAM data)
{
    if (code == HC_ACTION) {
        const auto* key = reinterpret_cast<KBDLLHOOKSTRUCT*>(data);
        const bool down = message == WM_KEYDOWN || message == WM_SYSKEYDOWN;
        const bool up = message == WM_KEYUP || message == WM_SYSKEYUP;
        if (key->vkCode == VK_SNAPSHOT && (down || up) && !g_snapshot_frozen) BeginSnapshot(15000);
        else if (down && key->vkCode == 'S' && (GetAsyncKeyState(VK_SHIFT) & 0x8000)
            && ((GetAsyncKeyState(VK_LWIN) | GetAsyncKeyState(VK_RWIN)) & 0x8000)) BeginSnapshot(15000);
        else if (down && key->vkCode == VK_ESCAPE && g_snapshot_frozen) EndSnapshot();
    }
    return CallNextHookEx(g_snapshot_hook, code, message, data);
}
