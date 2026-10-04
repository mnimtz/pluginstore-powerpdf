// http.h — minimal WinHTTP client: GET to string (UTF-8) and GET to file.
#pragma once
#include <string>

bool PSHttpGetText(const std::wstring& url, std::string& outUtf8, DWORD* status = nullptr);
bool PSHttpGetFile(const std::wstring& url, const std::wstring& targetPath, DWORD* status = nullptr);
// POST a UTF-8 JSON body; the response body is returned for every status
// (the server explains errors in it). True only for 2xx.
bool PSHttpPostJson(const std::wstring& url, const std::string& bodyUtf8, std::string& responseUtf8, DWORD* status = nullptr);
