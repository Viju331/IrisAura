// IrisAura.Face.CredentialProvider — INERT SKELETON (Phase 0A).
//
// This DLL deliberately exposes no COM classes and has no DllRegisterServer/DllUnregisterServer,
// so it cannot be registered or loaded by LogonUI as a credential provider. It exists only to prove
// that the native toolchain builds x64 and ARM64 binaries.
//
// Provider code is written only after the Phase 0C gate passes, and is tested only in a disposable
// VM (BLUEPRINT §3, §7.9, §10). Rules for that work: no camera/ML/network/UI code in this DLL,
// no exception may cross a COM boundary, and every failure path must fail closed.

#include <windows.h>

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID /*reserved*/) noexcept
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        // No per-thread work; avoid DLL_THREAD_ATTACH/DETACH notifications.
        ::DisableThreadLibraryCalls(module);
    }

    return TRUE;
}

// Standard COM exports, present so the export table shape is fixed early. Both are inert.

_Use_decl_annotations_
STDAPI DllCanUnloadNow()
{
    return S_OK;
}

_Use_decl_annotations_
STDAPI DllGetClassObject(REFCLSID /*clsid*/, REFIID /*iid*/, LPVOID* object)
{
    if (object != nullptr)
    {
        *object = nullptr;
    }

    return CLASS_E_CLASSNOTAVAILABLE;
}
