// http.h — minimal WinHTTP client: GET to string (UTF-8) and GET to file.
#pragma once
#include <string>

bool PSHttpGetText(const std::wstring& url, std::string& outUtf8, DWORD* status = nullptr);
bool PSHttpGetFile(const std::wstring& url, const std::wstring& targetPath, DWORD* status = nullptr);
