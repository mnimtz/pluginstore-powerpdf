// http.h — minimal WinHTTP client: GET to string (UTF-8) and GET to file.
#pragma once
#include <string>

bool PSHttpGetText(const std::wstring& url, std::string& outUtf8, DWORD* status = nullptr);
// Stops (and deletes the file) once more than maxBytes arrive.
bool PSHttpGetFile(const std::wstring& url, const std::wstring& targetPath, DWORD* status = nullptr,
                   unsigned long long maxBytes = 300ull * 1024 * 1024);
// POST a UTF-8 JSON body; the response body is returned for every status
// (the server explains errors in it). True only for 2xx.
// GET with the given customer code instead of the stored one (check before storing it).
bool PSHttpCheckCustomerCode(const std::wstring& url, const std::wstring& code, std::string& outUtf8, DWORD* status = nullptr,
                             size_t maxBytes = 64 * 1024);   // a whole catalog needs more (C1.4.2)
bool PSHttpPostJson(const std::wstring& url, const std::string& bodyUtf8, std::string& responseUtf8, DWORD* status = nullptr);
