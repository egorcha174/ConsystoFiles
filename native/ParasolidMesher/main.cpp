#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <userenv.h>
#include <aclapi.h>
#include <sddl.h>
#include <filesystem>
#include <string>
#include <vector>
#include <iostream>

namespace fs = std::filesystem;

static int setupFailure(const char* stage, DWORD error = GetLastError()) {
    std::cerr << stage << ": " << error << '\n';
    return 3;
}

struct Handle {
    HANDLE value = nullptr;
    ~Handle() { if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value); }
};

static std::wstring quote(const std::wstring& value) {
    std::wstring result = L"\"";
    size_t slashes = 0;
    for (wchar_t ch : value) {
        if (ch == L'\\') { ++slashes; continue; }
        result.append(ch == L'"' ? slashes * 2 + 1 : slashes, L'\\');
        slashes = 0;
        result += ch;
    }
    result.append(slashes * 2, L'\\');
    return result + L"\"";
}

static bool grant(const fs::path& path, PSID sid, DWORD rights, bool directory) {
    PACL oldAcl = nullptr, newAcl = nullptr;
    PSECURITY_DESCRIPTOR descriptor = nullptr;
    DWORD error = GetNamedSecurityInfoW(path.c_str(), SE_FILE_OBJECT,
        DACL_SECURITY_INFORMATION, nullptr, nullptr, &oldAcl, nullptr, &descriptor);
    if (error != ERROR_SUCCESS) return false;
    EXPLICIT_ACCESSW entry{};
    entry.grfAccessPermissions = rights;
    entry.grfAccessMode = GRANT_ACCESS;
    entry.grfInheritance = directory ? SUB_CONTAINERS_AND_OBJECTS_INHERIT : NO_INHERITANCE;
    entry.Trustee.TrusteeForm = TRUSTEE_IS_SID;
    entry.Trustee.TrusteeType = TRUSTEE_IS_UNKNOWN;
    entry.Trustee.ptstrName = static_cast<LPWSTR>(sid);
    error = SetEntriesInAclW(1, &entry, oldAcl, &newAcl);
    if (error == ERROR_SUCCESS)
        error = SetNamedSecurityInfoW(const_cast<LPWSTR>(path.c_str()), SE_FILE_OBJECT,
            DACL_SECURITY_INFORMATION, nullptr, nullptr, newAcl, nullptr);
    if (newAcl) LocalFree(newAcl);
    LocalFree(descriptor);
    return error == ERROR_SUCCESS;
}

static bool allowRuntime(const fs::path& runtime, PSID sid) {
    // Packaged installations already grant AppContainer read access. Portable copies may not.
    grant(runtime, sid, FILE_GENERIC_READ | FILE_GENERIC_EXECUTE, true);
    for (const auto& entry : fs::recursive_directory_iterator(runtime)) {
        const DWORD attributes = GetFileAttributesW(entry.path().c_str());
        if (attributes == INVALID_FILE_ATTRIBUTES || (attributes & FILE_ATTRIBUTE_REPARSE_POINT))
            return false;
        grant(entry.path(), sid, FILE_GENERIC_READ | FILE_GENERIC_EXECUTE, entry.is_directory());
    }
    return true;
}

static int run(const fs::path& input, const fs::path& output) {
    if (!input.is_absolute() || !output.is_absolute() || input.parent_path() != output.parent_path()
        || output.filename() != L"preview.csmesh" || input.stem() != L"input"
        || !fs::is_regular_file(input) || fs::file_size(input) > 32 * 1024 * 1024)
        return 2;
    wchar_t module[32768];
    const DWORD length = GetModuleFileNameW(nullptr, module, 32768);
    if (!length || length == 32768) return 2;
    const fs::path runtime = fs::path(module).parent_path();
    const fs::path work = input.parent_path();

    PSID sid = nullptr;
    HRESULT hr = CreateAppContainerProfile(L"Consysto.Files.Parasolid.Preview",
        L"Consysto Files Parasolid preview", L"Isolated CAD preview worker", nullptr, 0, &sid);
    if (hr == HRESULT_FROM_WIN32(ERROR_ALREADY_EXISTS))
        hr = DeriveAppContainerSidFromAppContainerName(L"Consysto.Files.Parasolid.Preview", &sid);
    if (FAILED(hr)) return setupFailure("container-profile", static_cast<DWORD>(hr));
    struct SidGuard { PSID sid; ~SidGuard() { FreeSid(sid); } } sidGuard{sid};
    if (!allowRuntime(runtime, sid) || !grant(work, sid,
        FILE_GENERIC_READ | FILE_GENERIC_WRITE | FILE_GENERIC_EXECUTE | DELETE, true))
        return setupFailure("container-access");

    PSECURITY_DESCRIPTOR label = nullptr;
    if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
        L"S:(ML;OICI;NW;;;LW)", SDDL_REVISION_1, &label, nullptr)) return setupFailure("work-label-descriptor");
    PACL sacl = nullptr;
    BOOL present = FALSE, defaulted = FALSE;
    GetSecurityDescriptorSacl(label, &present, &sacl, &defaulted);
    const DWORD labelError = SetNamedSecurityInfoW(const_cast<LPWSTR>(work.c_str()),
        SE_FILE_OBJECT, LABEL_SECURITY_INFORMATION, nullptr, nullptr, nullptr, sacl);
    LocalFree(label);
    if (labelError != ERROR_SUCCESS) return setupFailure("work-label", labelError);

    Handle job{CreateJobObjectW(nullptr, nullptr)};
    if (!job.value) return 3;
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
    limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        | JOB_OBJECT_LIMIT_ACTIVE_PROCESS | JOB_OBJECT_LIMIT_PROCESS_MEMORY;
    limits.BasicLimitInformation.ActiveProcessLimit = 1;
    limits.ProcessMemoryLimit = 512ull * 1024 * 1024;
    if (!SetInformationJobObject(job.value, JobObjectExtendedLimitInformation, &limits, sizeof(limits)))
        return 3;

    SIZE_T bytes = 0;
    InitializeProcThreadAttributeList(nullptr, 2, 0, &bytes);
    std::vector<BYTE> buffer(bytes);
    auto attributes = reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(buffer.data());
    if (!InitializeProcThreadAttributeList(attributes, 2, 0, &bytes)) return 3;
    struct AttributeGuard { LPPROC_THREAD_ATTRIBUTE_LIST p; ~AttributeGuard() { DeleteProcThreadAttributeList(p); } } attributeGuard{attributes};
    SECURITY_CAPABILITIES capabilities{};
    capabilities.AppContainerSid = sid;
    DWORD childPolicy = PROCESS_CREATION_CHILD_PROCESS_RESTRICTED;
    if (!UpdateProcThreadAttribute(attributes, 0, PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES,
        &capabilities, sizeof(capabilities), nullptr, nullptr)
        || !UpdateProcThreadAttribute(attributes, 0, PROC_THREAD_ATTRIBUTE_CHILD_PROCESS_POLICY,
        &childPolicy, sizeof(childPolicy), nullptr, nullptr)) return 3;

    const fs::path python = runtime / L"python.exe";
    std::wstring command = quote(python.wstring()) + L" -I -S -B "
        + quote((runtime / L"mesher.py").wstring()) + L" " + quote(input.wstring()) + L" " + quote(output.wstring());
    wchar_t system[32768];
    DWORD size = GetEnvironmentVariableW(L"SystemRoot", system, 32768);
    if (!size || size >= 32768) return 3;
    std::wstring environment = L"SystemRoot=" + std::wstring(system) + L'\0'
        + L"TEMP=" + work.wstring() + L'\0' + L"TMP=" + work.wstring() + L'\0';
    environment += L'\0';
    STARTUPINFOEXW startup{};
    startup.StartupInfo.cb = sizeof(startup);
    startup.lpAttributeList = attributes;
    PROCESS_INFORMATION process{};
    if (!CreateProcessW(python.c_str(), command.data(), nullptr, nullptr, FALSE,
        CREATE_NO_WINDOW | CREATE_SUSPENDED | EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT,
        environment.data(), work.c_str(), &startup.StartupInfo, &process)) return setupFailure("create-worker");
    Handle processHandle{process.hProcess}, threadHandle{process.hThread};
    if (!AssignProcessToJobObject(job.value, process.hProcess)) {
        TerminateProcess(process.hProcess, 3);
        return 3;
    }
    if (ResumeThread(process.hThread) == static_cast<DWORD>(-1)) return 3;
    if (WaitForSingleObject(process.hProcess, 18000) != WAIT_OBJECT_0) return 4;
    DWORD exitCode = 2;
    GetExitCodeProcess(process.hProcess, &exitCode);
    return exitCode == 0 ? 0 : 2;
}

int wmain(int argc, wchar_t** argv) {
    if (argc != 3) return 2;
    try { return run(fs::path(argv[1]), fs::path(argv[2])); }
    catch (const std::exception&) { return 2; }
}
