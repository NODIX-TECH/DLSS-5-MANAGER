// dll_trust.h - the gate every user-supplied runtime passes before LoadLibrary.
//
// The BYO folder (native\libraries\) exists so a user can run their own
// NVIDIA runtime build. It is also a writable directory next to an
// executable, which is exactly where a malicious DLL would like to sit.
// The gate answers three questions before anything is mapped:
//
//   1. Authenticode signature valid (WinVerifyTrust, no UI, revocation on).
//   2. The signer certificate chains to a trusted root through the
//      LOCAL-MACHINE store only - a root the current user installed does
//      not make a DLL trusted here (a compromised user account must not
//      produce a trusted runtime).
//   3. The signer is NVIDIA Corporation and the version resource's product
//      name starts with "NVIDIA".
//
// Plus the anti-swap hold: after verification the file is kept OPEN
// (FILE_SHARE_READ only) for the session. Verify-then-load has a race - a
// second process can swap the file between the trust check and LoadLibrary;
// the open handle wins it, because Windows denies the write while the
// handle exists.
//
// The verification only ever REFUSES a DLL; the NVIDIA runtimes themselves
// are never modified (they ship unmodified and stay droppable).
//
// Everything here is written from the WinTrust / WinVerifyTrust /
// CertGetCertificateChain documentation; no external source was copied
// (roadmap R9).

#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#include <softpub.h>
#include <wincrypt.h>
#include <wintrust.h>

#pragma comment(lib, "wintrust.lib")
#pragma comment(lib, "crypt32.lib")
#pragma comment(lib, "version.lib")

// The one signer the BYO folder accepts: NVIDIA signs its runtimes directly.
static const wchar_t *kTrustedSignerCn = L"NVIDIA Corporation";

// The product-name prefix the version resource carries ("NVIDIA DLSS-G",
// "NVIDIA NGX", and friends - pinning a marketing name would break on a
// rename).
static const wchar_t *kTrustedProductPrefix = L"NVIDIA";

// Handles kept open until the process exits, so the verified bytes are the
// bytes LoadLibrary maps.
static HANDLE g_trusted_file_handles[8] = {};
static int g_trusted_file_count = 0;

// The leaf's chain, machine roots only. Returns true when the chain
// validates against the Authenticode policy with the exclusive machine-root
// set. A per-user root cannot participate: hExclusiveRoot replaces the
// engine's root set instead of adding to it.
static bool NsChainMachineRootsOnly(PCCERT_CONTEXT leaf)
{
    if (leaf == nullptr) return false;

    HCERTSTORE machine_roots = CertOpenStore(
        CERT_STORE_PROV_SYSTEM_W, 0, 0,
        CERT_SYSTEM_STORE_LOCAL_MACHINE | CERT_STORE_READONLY_FLAG,
        L"Root");
    if (machine_roots == nullptr) return false;

    bool ok = false;
    HCERTCHAINENGINE engine = nullptr;
    CERT_CHAIN_ENGINE_CONFIG cfg = {};
    cfg.cbSize = sizeof(cfg);
    // hExclusiveRoot REPLACES the engine's root set: per-user roots are
    // outside it by construction.
    cfg.dwFlags = 0;
    cfg.hExclusiveRoot = machine_roots;
    if (CertCreateCertificateChainEngine(&cfg, &engine))
    {
        CERT_CHAIN_PARA para = {};
        para.cbSize = sizeof(para);
        PCCERT_CHAIN_CONTEXT chain = nullptr;
        if (CertGetCertificateChain(engine, leaf, nullptr, nullptr,
                                    &para, 0, nullptr, &chain))
        {
            CERT_CHAIN_POLICY_PARA pp = {};
            pp.cbSize = sizeof(pp);
            CERT_CHAIN_POLICY_STATUS ps = {};
            ps.cbSize = sizeof(ps);
            if (CertVerifyCertificateChainPolicy(
                    CERT_CHAIN_POLICY_AUTHENTICODE, chain, &pp, &ps)
                && ps.dwError == ERROR_SUCCESS)
                ok = true;
            CertFreeCertificateChain(chain);
        }
        CertFreeCertificateChainEngine(engine);
    }
    CertCloseStore(machine_roots, 0);
    return ok;
}

// Pull the leaf (signer) certificate out of the file's Authenticode blob.
static PCCERT_CONTEXT NsLeafCertificate(const wchar_t *path,
                                        HCERTSTORE *out_store)
{
    *out_store = nullptr;
    HCRYPTMSG msg = nullptr;
    DWORD encoding = 0, cert_type = 0, policy = 0;
    // For a signed PE the object comes back as PKCS7_SIGNED_EMBED: the
    // certificate OUT parameter is null - the signer certificate is found
    // through the message. CMSG_SIGNER_CERT_INFO_PARAM names the signer's
    // CERT_INFO; CertFindCertificateInStore then materialises the context
    // from the message's store (which carries every embedded certificate).
    if (!CryptQueryObject(CERT_QUERY_OBJECT_FILE, path,
                          CERT_QUERY_CONTENT_FLAG_ALL,
                          CERT_QUERY_FORMAT_FLAG_ALL, 0, &encoding,
                          &cert_type, &policy, out_store, &msg, nullptr))
        return nullptr;
    if (msg == nullptr) { CertCloseStore(*out_store, 0); *out_store = nullptr; return nullptr; }
    DWORD size = 0;
    if (!CryptMsgGetParam(msg, CMSG_SIGNER_CERT_INFO_PARAM, 0, nullptr, &size)
        || size == 0)
    { CryptMsgClose(msg); CertCloseStore(*out_store, 0); *out_store = nullptr; return nullptr; }
    CERT_INFO *info = (CERT_INFO *)HeapAlloc(GetProcessHeap(), 0, size);
    if (!info) { CryptMsgClose(msg); CertCloseStore(*out_store, 0); *out_store = nullptr; return nullptr; }
    PCCERT_CONTEXT leaf = nullptr;
    if (CryptMsgGetParam(msg, CMSG_SIGNER_CERT_INFO_PARAM, 0, info, &size)
        && *out_store != nullptr)
    {
        leaf = CertFindCertificateInStore(*out_store, X509_ASN_ENCODING
                                          | PKCS_7_ASN_ENCODING, 0,
                                          CERT_FIND_SUBJECT_CERT, info, nullptr);
    }
    HeapFree(GetProcessHeap(), 0, info);
    CryptMsgClose(msg);
    return leaf;
}

// Verify a DLL's Authenticode signature end to end: valid chain to a
// machine root, NVIDIA signer, NVIDIA product name. True = loadable.
static bool NsTrustedDll(const wchar_t *path)
{
    // ---- Authenticode -------------------------------------------------
    WINTRUST_FILE_INFO file = {};
    file.cbStruct = sizeof(file);
    file.pcwszFilePath = path;

    WINTRUST_DATA wd = {};
    wd.cbStruct = sizeof(wd);
    wd.dwUIChoice = WTD_UI_NONE;
    // Whole-chain revocation: a revoked intermediate or root must fail the
    // gate, not just a revoked leaf. Costs a CRL fetch on first sight of a
    // DLL; the result is cached by Windows for the session.
    wd.fdwRevocationChecks = WTD_REVOKE_WHOLECHAIN;
    wd.dwUnionChoice = WTD_CHOICE_FILE;
    wd.pFile = &file;
    wd.dwStateAction = WTD_STATEACTION_VERIFY;

    GUID action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
    LONG status = WinVerifyTrust((HWND)INVALID_HANDLE_VALUE, &action, &wd);
    wd.dwStateAction = WTD_STATEACTION_CLOSE;
    WinVerifyTrust(nullptr, &action, &wd);
    if (status != ERROR_SUCCESS)
        return false;

    // ---- the signer, through the machine-root chain -------------------
    HCERTSTORE store = nullptr;
    PCCERT_CONTEXT leaf = NsLeafCertificate(path, &store);
    if (leaf == nullptr) return false;

    bool signer_ok = false;
    {
        DWORD len = CertGetNameStringW(leaf, CERT_NAME_SIMPLE_DISPLAY_TYPE,
                                       0, nullptr, nullptr, 0);
        if (len > 0)
        {
            wchar_t *name = (wchar_t *)HeapAlloc(GetProcessHeap(), 0,
                                                 len * sizeof(wchar_t));
            if (name)
            {
                if (CertGetNameStringW(leaf, CERT_NAME_SIMPLE_DISPLAY_TYPE,
                                       0, nullptr, name, len) > 0
                    && wcscmp(name, kTrustedSignerCn) == 0)
                    signer_ok = true;
                HeapFree(GetProcessHeap(), 0, name);
            }
        }
    }
    if (!signer_ok)
    { CertFreeCertificateContext(leaf); CertCloseStore(store, 0); return false; }

    bool chain_ok = NsChainMachineRootsOnly(leaf);
    CertFreeCertificateContext(leaf);
    CertCloseStore(store, 0);
    if (!chain_ok) return false;

    // ---- product name --------------------------------------------------
    DWORD handle = 0;
    DWORD size = GetFileVersionInfoSizeW(path, &handle);
    if (size == 0) return false;
    unsigned char *block = (unsigned char *)HeapAlloc(GetProcessHeap(), 0,
                                                      size);
    if (!block) return false;
    bool product_ok = false;
    if (GetFileVersionInfoW(path, 0, size, block))
    {
        struct LANGCODEPAGE { WORD lang; WORD code; } *lcp = nullptr;
        UINT lcp_len = 0;
        if (VerQueryValueW(block, L"\\VarFileInfo\\Translation",
                           (void **)&lcp, &lcp_len) && lcp_len > 0
            && lcp_len >= sizeof(*lcp))
        {
            wchar_t query[256];
            _snwprintf_s(query, 256, _TRUNCATE,
                         L"\\StringFileInfo\\%04x%04x\\ProductName",
                         lcp[0].lang, lcp[0].code);
            wchar_t *product = nullptr;
            UINT plen = 0;
            if (VerQueryValueW(block, query, (void **)&product, &plen)
                && product && plen > 0
                && wcsncmp(product, kTrustedProductPrefix,
                           wcslen(kTrustedProductPrefix)) == 0)
                product_ok = true;
        }
    }
    HeapFree(GetProcessHeap(), 0, block);
    if (!product_ok) return false;

    // ---- the anti-swap hold --------------------------------------------
    HANDLE hold = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ,
                              nullptr, OPEN_EXISTING,
                              FILE_ATTRIBUTE_NORMAL, nullptr);
    if (hold != INVALID_HANDLE_VALUE
        && g_trusted_file_count < (int)(sizeof(g_trusted_file_handles)
                                        / sizeof(g_trusted_file_handles[0])))
        g_trusted_file_handles[g_trusted_file_count++] = hold;
    return true;
}

// The one call a loader makes: verify a user-writable path before
// LoadLibrary. Returns true when the path may be loaded. The reason goes to
// the caller's log line (the header is included before the Log definition,
// so it must not log itself); a DLL is never modified here - only refused.
static bool NsGateByoDll(const wchar_t *path, const char *what)
{
    return NsTrustedDll(path);
}