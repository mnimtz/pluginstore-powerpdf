// catalog.h — fetch and parse the store catalog.
//
// The server serves a tab-separated variant of /api/catalog for native
// clients (format=tsv), so no JSON parser is needed here. One line per
// package:
//   id \t version \t channel \t name \t description \t changelog \t
//   minPowerPdfVersion \t sizeBytes \t sha256 \t downloadUrl \t zxtName
//   (then category, author, contact, category name, icon URL, rating,
//   rating count, screenshots, customer; see PSCatalogEntry)

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
    std::wstring zxtName;        // plugin binary base name, e.g. "SmartBookmarks"
    std::wstring category;
    std::wstring author;
    std::wstring contactEmail;
    std::wstring categoryName;   // localized by the server (TSV column 15)
    std::wstring iconUrl;        // GET /api/packages/{id}/icon (TSV column 16)
    double rating = 0;           // average stars, 0 = none (TSV column 17, server 0.11+)
    int ratingCount = 0;         // number of ratings (TSV column 18)
    int screenshots = 0;         // number of screenshots (TSV column 19)
    std::wstring customer;       // customer name when delivered by a customer code (TSV column 20)
    std::wstring signature;      // "keyId:base64(r||s)" of the server (TSV column 21, see signature.h)
    bool noUi = false;           // "ui": "none": no ribbon buttons (TSV column 22, server 1.1.1+)
    std::wstring connections;    // "connects to" as ASCII JSON (TSV column 23, server 1.19.0+), empty = not declared
    std::wstring installedVersion; // filled by the install module, empty = not installed
    bool orphan = false;           // installed by the store, no longer in the catalog (C1.4.1): remove only
};

bool PSFetchCatalog(std::vector<PSCatalogEntry>& out, std::wstring& error);
// Same without host calls (any thread): lang is the host language code.
bool PSFetchCatalogFor(const std::wstring& lang, std::vector<PSCatalogEntry>& out, std::wstring& error,
                       const std::wstring* codes = nullptr);   // codes: instead of the stored ones (C1.4.1)

// C1.9.3: the last catalog fetched with the stored codes, so the store window can show it at
// once and load a fresh one behind it: from memory when it is at most maxAgeMs old, else from
// the local copy of the last run (any age). False when there is none for this language, server,
// channel and codes. Installed versions are read again. Any thread.
bool PSCachedCatalog(const std::wstring& lang, unsigned long maxAgeMs, std::vector<PSCatalogEntry>& out);

// 1 to 64 letters, digits, '-' or '_' (the server enforces the same rule).
bool PSIsValidZxtName(const std::wstring& name);
