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
    std::wstring categoryName;   // localized by the server (TSV column 15)
    std::wstring iconUrl;        // GET /api/packages/{id}/icon (TSV column 16)
    double rating = 0;           // average stars, 0 = none (TSV column 17, server 0.11+)
    int ratingCount = 0;         // number of ratings (TSV column 18)
    int screenshots = 0;         // number of screenshots (TSV column 19)
    std::wstring installedVersion; // filled by the install module, empty = not installed
};

bool PSFetchCatalog(std::vector<PSCatalogEntry>& out, std::wstring& error);
