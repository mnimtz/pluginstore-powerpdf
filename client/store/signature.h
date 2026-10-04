// signature.h - catalog signatures of the store server (ECDSA P-256, SHA-256).
//
// The server signs "addonstore-pkg-v2\n{id}\n{version}\n{sha256}\n{zxtName}" of every
// catalog entry (TSV column 21: "keyId:base64(r||s)"). The client installs
// only packages signed with a key it trusts, so a server address changed in
// the user profile cannot deliver foreign packages:
//   - keys built into the client (the store instance and an offline
//     recovery key),
//   - HKLM ...\PluginStore\Policies\Store\TrustedSigningKeys (REG_SZ or
//     REG_MULTI_SZ, "keyId:base64(X||Y)" separated by ';' or one per line)
//     for a company's own store instance,
//   - HKLM policy AllowUnsigned = 1 switches the check off (test servers).
// Nothing under HKCU can add a key.

#pragma once
#include <string>

struct PSCatalogEntry;

bool PSSignatureValid(const PSCatalogEntry& e);
