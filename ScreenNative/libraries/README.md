# Libraries (BYO - bring your own)

Place your own NVIDIA runtime DLLs here; they win over the bundled copies
in the parent (`native/`) directory. Files are loaded by exact name:

- `nvngx_dlssg.dll` - DLSS Frame Generation (Settings -> Processing ->
  DLSS Frame Generation). Without it the FG switch reports
  "nvngx_dlssg.dll load failed" and stays off.
- `nvngx_dlssnr.dll` - the Neural Rendering runtime. Override also
  possible via the `nr_dll` config key (path) or the `NS_NR_DLL`
  environment variable - those take priority over this folder.

Where to get DLLs: DLSS Swapper, or the official NVIDIA/DLSS GitHub
repository (`lib/Windows_x86_64/rel/`). Match the DLL's architecture
requirements - the app reports supported kernels at startup.

Removed with the library auto-updater (2026-09-14): the app no longer
downloads anything on its own; it stays fully offline.

---

# Библиотеки (устанавливаются вручную)

Положите свои DLL рантаймы NVIDIA сюда; файлы из этой папки имеют
приоритет над копиями в родительской (`native/`). Загрузка идёт по
точному имени файла:

- `nvngx_dlssg.dll` - DLSS Frame Generation (Настройки -> Обработка ->
  DLSS Frame Generation). Без него переключатель сообщит
  "nvngx_dlssg.dll load failed" и останется выключенным.
- `nvngx_dlssnr.dll` - рантайм нейрорендеринга. Альтернативный
  переопределитель - ключ `nr_dll` в config.json (путь) или переменная
  окружения `NS_NR_DLL` - они имеют приоритет над этой папкой.

Где взять: DLSS Swapper или официальный репозиторий NVIDIA/DLSS на
GitHub (`lib/Windows_x86_64/rel/`). Следите за архитектурными
требованиями DLL - приложение проверяет поддержку при старте.

Выпущено вместе с удалением автообновления библиотек (14.09.2026):
программа больше ничего не скачивает сама и остаётся полностью офлайн.
