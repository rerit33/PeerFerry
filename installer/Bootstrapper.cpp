#define UNICODE
#define _UNICODE
#define NOMINMAX
#include <windows.h>
#include <commctrl.h>
#include <shlobj.h>
#include <shlwapi.h>
#include <shellapi.h>
#include <algorithm>
namespace Gdiplus { using std::min; using std::max; }
#pragma warning(push)
#pragma warning(disable:4458)
#include <gdiplus.h>
#pragma warning(pop)
#include <wrl/client.h>
#include <filesystem>
#include <fstream>
#include <string>
#include <thread>
#include <vector>
#include <memory>

#ifndef APP_ID
#define APP_ID L"PeerFerry"
#endif
#ifndef APP_TITLE
#define APP_TITLE L"PeerFerry"
#endif
#ifndef APP_VERSION
#define APP_VERSION L"0.2.1"
#endif

namespace fs = std::filesystem;
using Microsoft::WRL::ComPtr;
struct Failure { std::wstring message; };
struct Options {
    bool desktop = true, startMenu = true, silent = false;
    fs::path installTo, log, preview;
};
struct Control { HWND window; int x, y, width, height; };
static Options options;
static HWND windowHandle, desktopBox, startMenuBox, actionButton, cancelButton, statusLabel, progressBar, heading;
static HFONT normalFont, headingFont;
static UINT dpi = 96;
static bool busy = false, installed = false;
static std::vector<Control> controls;
static std::thread worker;
static constexpr UINT Finished = WM_APP + 1;
static constexpr int Install = 201, Cancel = 202;

static std::wstring WindowsError(const std::wstring& action) {
    wchar_t* message = nullptr;
    FormatMessageW(FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS,
        nullptr, GetLastError(), 0, reinterpret_cast<wchar_t*>(&message), 0, nullptr);
    auto result = action + (message ? L"\n" + std::wstring(message) : L"");
    if (message) LocalFree(message);
    return result;
}
static fs::path KnownFolder(REFKNOWNFOLDERID id) {
    PWSTR value = nullptr;
    if (FAILED(SHGetKnownFolderPath(id, KF_FLAG_DEFAULT, nullptr, &value))) throw Failure{L"Не удалось определить пользовательскую папку Windows."};
    fs::path path(value); CoTaskMemFree(value); return path;
}
static void Log(const Options& config, const std::wstring& message) {
    int count = WideCharToMultiByte(CP_UTF8, 0, message.c_str(), static_cast<int>(message.size()), nullptr, 0, nullptr, nullptr);
    std::string utf8(count, '\0'); WideCharToMultiByte(CP_UTF8, 0, message.c_str(), static_cast<int>(message.size()), utf8.data(), count, nullptr, nullptr);
    std::ofstream stream(config.log, std::ios::app | std::ios::binary); stream << utf8 << '\n';
}
static bool Inside(const fs::path& path, const fs::path& root) {
    auto full = fs::absolute(path).lexically_normal().wstring();
    auto prefix = fs::absolute(root).lexically_normal().wstring() + L"\\";
    return full.size() >= prefix.size() && _wcsnicmp(full.c_str(), prefix.c_str(), prefix.size()) == 0;
}
static void RemoveUnselectedShortcut(const fs::path& path, const fs::path& root) {
    if (!fs::exists(path)) return;
    ComPtr<IShellLinkW> link; ComPtr<IPersistFile> persistence;
    if (FAILED(CoCreateInstance(CLSID_ShellLink, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&link))) ||
        FAILED(link.As(&persistence)) || FAILED(persistence->Load(path.c_str(), STGM_READ))) throw Failure{L"Не удалось проверить ярлык: " + path.wstring()};
    wchar_t target[32768] = {};
    if (FAILED(link->GetPath(target, ARRAYSIZE(target), nullptr, SLGP_RAWPATH)) || !Inside(target, root))
        throw Failure{L"Ярлык принадлежит другой установке и не был изменён: " + path.wstring()};
    persistence.Reset(); link.Reset();
    if (!DeleteFileW(path.c_str())) throw Failure{WindowsError(L"Не удалось убрать невыбранный ярлык.")};
    SHChangeNotify(SHCNE_DELETE, SHCNF_PATHW, path.c_str(), nullptr);
}
static void VerifySelectedShortcut(const fs::path& path, const fs::path& root) {
    ComPtr<IShellLinkW> link; ComPtr<IPersistFile> persistence;
    if (FAILED(CoCreateInstance(CLSID_ShellLink, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&link))) ||
        FAILED(link.As(&persistence)) || FAILED(persistence->Load(path.c_str(), STGM_READ))) throw Failure{L"Не удалось создать выбранный ярлык: " + path.wstring()};
    wchar_t target[32768] = {};
    if (FAILED(link->GetPath(target, ARRAYSIZE(target), nullptr, SLGP_RAWPATH)) || !Inside(target, root) || !fs::exists(target))
        throw Failure{L"Выбранный ярлык не ведёт к установленному приложению: " + path.wstring()};
}
static void InstallApplication(const Options& config) {
    GUID guid; if (FAILED(CoCreateGuid(&guid))) throw Failure{L"Не удалось подготовить временную папку."};
    wchar_t suffix[40]; StringFromGUID2(guid, suffix, ARRAYSIZE(suffix));
    auto temporary = fs::temp_directory_path() / (std::wstring(L"PeerFerry-setup-") + suffix);
    if (!fs::create_directory(temporary)) throw Failure{L"Не удалось создать временную папку установщика."};
    auto backend = temporary / L"Install.exe";
    try {
        HRSRC resource = FindResourceW(nullptr, MAKEINTRESOURCEW(102), RT_RCDATA);
        HGLOBAL loaded = resource ? LoadResource(nullptr, resource) : nullptr;
        auto data = loaded ? LockResource(loaded) : nullptr;
        DWORD size = resource ? SizeofResource(nullptr, resource) : 0;
        if (!data || !size) throw Failure{L"В установщике отсутствует пакет приложения."};
        HANDLE file = CreateFileW(backend.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE) throw Failure{WindowsError(L"Не удалось подготовить пакет.")};
        DWORD written = 0; BOOL saved = WriteFile(file, data, size, &written, nullptr); CloseHandle(file);
        if (!saved || written != size) throw Failure{L"Не удалось полностью записать пакет установки."};
        auto command = L"\"" + backend.wstring() + L"\" --silent --installto \"" + config.installTo.wstring() + L"\" --log \"" + config.log.wstring() + L"\"";
        STARTUPINFOW startup{}; startup.cb = sizeof(startup); startup.dwFlags = STARTF_USESHOWWINDOW; startup.wShowWindow = SW_HIDE;
        PROCESS_INFORMATION process{};
        if (!CreateProcessW(backend.c_str(), command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, temporary.c_str(), &startup, &process))
            throw Failure{WindowsError(L"Не удалось запустить установку.")};
        CloseHandle(process.hThread); WaitForSingleObject(process.hProcess, INFINITE);
        DWORD result = 1; GetExitCodeProcess(process.hProcess, &result); CloseHandle(process.hProcess);
        if (result != 0 || !fs::exists(config.installTo / L"current" / L"PeerFerry.exe"))
            throw Failure{L"Установка не завершена. Подробности в журнале:\n" + config.log.wstring()};
        // Velopack creates both locations. Removing an unchecked location now is respected
        // by later updates, which only recreate newly introduced shortcut locations.
        if (!config.desktop) RemoveUnselectedShortcut(KnownFolder(FOLDERID_Desktop) / (std::wstring(APP_TITLE) + L".lnk"), config.installTo);
        else VerifySelectedShortcut(KnownFolder(FOLDERID_Desktop) / (std::wstring(APP_TITLE) + L".lnk"), config.installTo);
        if (!config.startMenu) RemoveUnselectedShortcut(KnownFolder(FOLDERID_Programs) / (std::wstring(APP_TITLE) + L".lnk"), config.installTo);
        else VerifySelectedShortcut(KnownFolder(FOLDERID_Programs) / (std::wstring(APP_TITLE) + L".lnk"), config.installTo);
        Log(config, L"PeerFerry setup complete. Desktop=" + std::to_wstring(config.desktop) + L" StartMenu=" + std::to_wstring(config.startMenu));
    }
    catch (...) { std::error_code ignored; fs::remove(backend, ignored); fs::remove(temporary, ignored); throw; }
    std::error_code ignored; fs::remove(backend, ignored); fs::remove(temporary, ignored);
}
static int Scale(int value) { return MulDiv(value, static_cast<int>(dpi), 96); }
static void SetFonts() {
    if (normalFont) DeleteObject(normalFont); if (headingFont) DeleteObject(headingFont);
    normalFont = CreateFontW(-Scale(14), 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET, 0, 0, CLEARTYPE_QUALITY, 0, L"Segoe UI");
    headingFont = CreateFontW(-Scale(24), 0, 0, 0, FW_SEMIBOLD, FALSE, FALSE, FALSE, DEFAULT_CHARSET, 0, 0, CLEARTYPE_QUALITY, 0, L"Segoe UI");
    for (auto& control : controls) SendMessageW(control.window, WM_SETFONT, reinterpret_cast<WPARAM>(control.window == heading ? headingFont : normalFont), TRUE);
}
static HWND AddControl(LPCWSTR type, LPCWSTR text, DWORD style, int x, int y, int width, int height, int id = 0) {
    auto child = CreateWindowExW(0, type, text, WS_CHILD | WS_VISIBLE | style, Scale(x), Scale(y), Scale(width), Scale(height), windowHandle, reinterpret_cast<HMENU>(static_cast<INT_PTR>(id)), GetModuleHandleW(nullptr), nullptr);
    controls.push_back({child, x, y, width, height}); return child;
}
static void StartInstall() {
    options.desktop = SendMessageW(desktopBox, BM_GETCHECK, 0, 0) == BST_CHECKED;
    options.startMenu = SendMessageW(startMenuBox, BM_GETCHECK, 0, 0) == BST_CHECKED;
    busy = true; EnableWindow(desktopBox, FALSE); EnableWindow(startMenuBox, FALSE); EnableWindow(actionButton, FALSE); EnableWindow(cancelButton, FALSE);
    SetWindowTextW(statusLabel, L"Устанавливаем PeerFerry…"); ShowWindow(progressBar, SW_SHOW); SendMessageW(progressBar, PBM_SETMARQUEE, TRUE, 30);
    worker = std::thread([config = options] {
        CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
        auto failure = new std::wstring;
        try { InstallApplication(config); }
        catch (const Failure& error) { *failure = error.message; }
        catch (...) { *failure = L"Не удалось завершить установку. Журнал: " + config.log.wstring(); }
        CoUninitialize(); PostMessageW(windowHandle, Finished, 0, reinterpret_cast<LPARAM>(failure));
    });
}
static LRESULT CALLBACK WindowProcedure(HWND window, UINT message, WPARAM wparam, LPARAM lparam) {
    switch (message) {
    case WM_CREATE: {
        windowHandle = window; dpi = GetDpiForWindow(window);
        auto icon = AddControl(L"STATIC", L"", SS_ICON, 24, 24, 40, 40);
        SendMessageW(icon, STM_SETICON, reinterpret_cast<WPARAM>(LoadIconW(GetModuleHandleW(nullptr), MAKEINTRESOURCEW(101))), 0);
        heading = AddControl(L"STATIC", L"PeerFerry", 0, 80, 22, 430, 38);
        AddControl(L"STATIC", L"Выберите, где создать ярлыки приложения.", 0, 80, 67, 460, 24);
        desktopBox = AddControl(L"BUTTON", L"На рабочем столе", BS_AUTOCHECKBOX | WS_TABSTOP, 80, 111, 400, 28);
        startMenuBox = AddControl(L"BUTTON", L"В меню «Пуск»", BS_AUTOCHECKBOX | WS_TABSTOP, 80, 151, 400, 28);
        SendMessageW(desktopBox, BM_SETCHECK, options.desktop ? BST_CHECKED : BST_UNCHECKED, 0);
        SendMessageW(startMenuBox, BM_SETCHECK, options.startMenu ? BST_CHECKED : BST_UNCHECKED, 0);
        statusLabel = AddControl(L"STATIC", L"Установка для текущего пользователя · версия " APP_VERSION, 0, 24, 205, 512, 45);
        progressBar = AddControl(PROGRESS_CLASSW, L"", PBS_MARQUEE, 24, 252, 512, 7); ShowWindow(progressBar, SW_HIDE);
        cancelButton = AddControl(L"BUTTON", L"Отмена", BS_PUSHBUTTON | WS_TABSTOP, 224, 280, 110, 36, Cancel);
        actionButton = AddControl(L"BUTTON", L"Установить", BS_DEFPUSHBUTTON | WS_TABSTOP, 346, 280, 190, 36, Install);
        SetFonts(); return 0;
    }
    case WM_COMMAND:
        if ((LOWORD(wparam) == Cancel || LOWORD(wparam) == IDCANCEL) && !busy) DestroyWindow(window);
        else if (LOWORD(wparam) == Install && !busy) {
            if (installed) {
                auto executable = options.installTo / L"current" / L"PeerFerry.exe";
                auto result = reinterpret_cast<INT_PTR>(ShellExecuteW(window, L"open", executable.c_str(), nullptr, executable.parent_path().c_str(), SW_SHOWNORMAL));
                if (result > 32) DestroyWindow(window); else MessageBoxW(window, L"Не удалось запустить PeerFerry. Используйте созданный ярлык.", L"PeerFerry", MB_OK | MB_ICONERROR);
            } else StartInstall();
        }
        return 0;
    case Finished: {
        if (worker.joinable()) worker.join();
        std::unique_ptr<std::wstring> error(reinterpret_cast<std::wstring*>(lparam)); busy = false;
        SendMessageW(progressBar, PBM_SETMARQUEE, FALSE, 0); ShowWindow(progressBar, SW_HIDE); EnableWindow(cancelButton, TRUE); EnableWindow(actionButton, TRUE);
        if (error->empty()) { installed = true; SetWindowTextW(statusLabel, L"Готово. PeerFerry установлен, выбранные ярлыки созданы."); SetWindowTextW(actionButton, L"Запустить PeerFerry"); SetWindowTextW(cancelButton, L"Закрыть"); }
        else { EnableWindow(desktopBox, TRUE); EnableWindow(startMenuBox, TRUE); SetWindowTextW(statusLabel, L"Установка не завершена. Можно повторить попытку."); MessageBoxW(window, error->c_str(), L"Установка PeerFerry", MB_OK | MB_ICONERROR); }
        return 0;
    }
    case WM_DPICHANGED: {
        dpi = HIWORD(wparam); auto rectangle = reinterpret_cast<RECT*>(lparam);
        SetWindowPos(window, nullptr, rectangle->left, rectangle->top, rectangle->right - rectangle->left, rectangle->bottom - rectangle->top, SWP_NOZORDER | SWP_NOACTIVATE);
        for (auto& control : controls) MoveWindow(control.window, Scale(control.x), Scale(control.y), Scale(control.width), Scale(control.height), TRUE);
        SetFonts(); return 0;
    }
    case WM_CTLCOLORSTATIC: case WM_CTLCOLORBTN:
        SetBkColor(reinterpret_cast<HDC>(wparam), RGB(255,255,255)); return reinterpret_cast<LRESULT>(GetStockObject(WHITE_BRUSH));
    case WM_CLOSE: if (!busy) DestroyWindow(window); return 0;
    case WM_DESTROY: if (normalFont) DeleteObject(normalFont); if (headingFont) DeleteObject(headingFont); PostQuitMessage(0); return 0;
    }
    return DefWindowProcW(window, message, wparam, lparam);
}
static void CapturePreview(HWND window, const fs::path& path) {
    Gdiplus::GdiplusStartupInput input; ULONG_PTR token = 0; Gdiplus::GdiplusStartup(&token, &input, nullptr);
    RECT bounds; GetWindowRect(window, &bounds); auto dc = GetDC(window); auto memory = CreateCompatibleDC(dc);
    auto bitmap = CreateCompatibleBitmap(dc, bounds.right-bounds.left, bounds.bottom-bounds.top); auto previous = SelectObject(memory, bitmap);
    PrintWindow(window, memory, 2);
    CLSID png; CLSIDFromString(L"{557cf406-1a04-11d3-9a73-0000f81ef32e}", &png);
    { Gdiplus::Bitmap image(bitmap, nullptr); if (image.Save(path.c_str(), &png) != Gdiplus::Ok) throw Failure{L"Не удалось сохранить PNG установщика."}; }
    SelectObject(memory, previous); DeleteObject(bitmap); DeleteDC(memory); ReleaseDC(window, dc); Gdiplus::GdiplusShutdown(token);
}
int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR, int) {
    SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32); SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    try {
        options.installTo = KnownFolder(FOLDERID_LocalAppData) / APP_ID;
        options.log = fs::temp_directory_path() / L"PeerFerry-install.log";
        int count; auto args = CommandLineToArgvW(GetCommandLineW(), &count);
        for (int i=1; i<count; i++) {
            std::wstring argument(args[i]);
            if (argument == L"--silent") options.silent = true;
            else if (i+1<count && argument == L"--installto") options.installTo = fs::absolute(args[++i]);
            else if (i+1<count && argument == L"--log") options.log = fs::absolute(args[++i]);
            else if (i+1<count && argument == L"--preview") options.preview = fs::absolute(args[++i]);
            else if (i+1<count && (argument == L"--desktop-shortcut" || argument == L"--start-menu-shortcut")) {
                std::wstring value(args[++i]); if (value != L"yes" && value != L"no") throw Failure{L"Значение параметра ярлыка должно быть yes или no."};
                (argument == L"--desktop-shortcut" ? options.desktop : options.startMenu) = value == L"yes";
            } else throw Failure{L"Неизвестный или неполный параметр: " + argument};
        }
        LocalFree(args);
        if (options.silent) { InstallApplication(options); CoUninitialize(); return 0; }
        INITCOMMONCONTROLSEX common{sizeof(common), ICC_PROGRESS_CLASS | ICC_STANDARD_CLASSES}; InitCommonControlsEx(&common);
        WNDCLASSEXW type{sizeof(type)}; type.hInstance = instance; type.lpfnWndProc = WindowProcedure; type.lpszClassName = L"PeerFerryInstaller";
        type.hIcon = LoadIconW(instance, MAKEINTRESOURCEW(101)); type.hIconSm = type.hIcon; type.hCursor = LoadCursorW(nullptr, IDC_ARROW); type.hbrBackground = static_cast<HBRUSH>(GetStockObject(WHITE_BRUSH)); RegisterClassExW(&type);
        dpi = GetDpiForSystem(); RECT bounds{0,0,Scale(560),Scale(340)}; DWORD style=WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX;
        AdjustWindowRectExForDpi(&bounds, style, FALSE, 0, dpi); RECT area; SystemParametersInfoW(SPI_GETWORKAREA, 0, &area, 0);
        auto window = CreateWindowExW(0, type.lpszClassName, L"Установка PeerFerry", style, (area.right-(bounds.right-bounds.left))/2, (area.bottom-(bounds.bottom-bounds.top))/2, bounds.right-bounds.left, bounds.bottom-bounds.top, nullptr, nullptr, instance, nullptr);
        if (!window) throw Failure{WindowsError(L"Не удалось открыть окно установщика.")};
        ShowWindow(window, options.preview.empty() ? SW_SHOWNORMAL : SW_SHOWNOACTIVATE); UpdateWindow(window);
        if (options.preview.empty()) SetFocus(actionButton);
        if (!options.preview.empty()) { CapturePreview(window, options.preview); DestroyWindow(window); CoUninitialize(); return 0; }
        MSG message; while (GetMessageW(&message, nullptr, 0, 0)>0) { if (!IsDialogMessageW(window, &message)) { TranslateMessage(&message); DispatchMessageW(&message); } }
        CoUninitialize(); return 0;
    }
    catch (const Failure& error) { if (options.silent) Log(options, error.message); else MessageBoxW(nullptr, error.message.c_str(), L"Установка PeerFerry", MB_OK | MB_ICONERROR); }
    catch (...) { if (!options.silent) MessageBoxW(nullptr, L"Не удалось запустить установщик PeerFerry.", L"PeerFerry", MB_OK | MB_ICONERROR); }
    CoUninitialize(); return 1;
}
