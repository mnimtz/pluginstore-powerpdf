// catalog.h — fetch and parse the store catalog.
//
// The server serves a tab-separated variant of /api/catalog for native
// clients (format=tsv), so no JSON parser is needed here. One line per
// package:
//   id \t version \t channel \t name \t description \t changelog \t
//   minPowerPdfVersion \t sizeBytes \t sha256 \t downloadUrl \t zxtName

#pragma once
#include <string>
#include <vector>

struct PSCatalogEntry
{
    std::wstring id;
    std::wstring version;
    std::wstring channel;        // "live" or "beta"
    std::wstring name;
    std::wstring description;
    std::wstring changelog;
    std::wstring minHost;
    unsigned long long sizeBytes = 0;
    std::wstring sha256;
    std::wstring downloadUrl;
    std::wstring zxtName;        // plugin binary base name, e.g. "OfficeKonverter"
    std::wstring category;
    std::wstring author;
    std::wstring contactEmail;
    std::wstring installedVersion; // filled by the install module, empty = not installed
};

bool PSFetchCatalog(std::vector<PSCatalogEntry>& out, std::wstring& error);
