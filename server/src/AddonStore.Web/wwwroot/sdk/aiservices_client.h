// aiservices_client.h - header-only helper for plug-ins that use the AI Plug-in (KI-Plugin).
//
// MIT License. Copyright (c) 2026 Tungsten Automation.
// Permission is hereby granted, free of charge, to any person obtaining a copy of this file,
// to deal in it without restriction, including the rights to use, copy, modify, merge,
// publish, distribute, sublicense and/or sell copies, subject to this notice being kept.
// THE FILE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND.
// Download: https://addon.power-pdf.de/api/sdk/aiservices_client.h
//
// Include after the Power PDF SDK headers (PIHeaders.h). Typical use:
//
//     static AISClient g_ai;                       // one per plug-in
//     if (g_ai.Available()) { ...show "Use AI"... }
//     else                  { ...show "Install the AI Plug-in from the Add-on Store"... }
//
//     AISRequest r = AISClient::MakeRequest("SmartCompare", "Änderungen zusammenfassen");
//     r.user = prompt.c_str();
//     g_ai.Submit(r, &OnDone, this, &job);
//
// The lookup is lazy and repeated until it succeeds, because plug-in start
// order is not guaranteed.
#pragma once
#include "aiservices.h"
#include <string.h>

class AISClient
{
public:
    // True when the KI-Plugin is installed and offers at least version 1.
    bool Available() { return Hft() != NULL; }

    AISStatus GetCaps(const char* pluginId, uint32_t modelClass, AISCaps* caps)
    {
        if (!caps) return AIS_INVALID_ARGUMENT;
        memset(caps, 0, sizeof *caps);
        caps->size = sizeof *caps;
        HFT h = Hft();
        if (!h) return AIS_NOT_CONFIGURED;
        return ((AISGetCapsProc)h[AISSel_GetCaps])(pluginId, modelClass, caps);
    }

    AISStatus RequestPermission(const char* pluginId, const char* pluginName,
                                const char* purpose, HWND owner)
    {
        HFT h = Hft();
        if (!h) return AIS_NOT_CONFIGURED;
        return ((AISRequestPermissionProc)h[AISSel_RequestPermission])(pluginId, pluginName, purpose, owner);
    }

    AISStatus Submit(const AISRequest& req, AISDoneProc done, void* clientData, AISJob* job)
    {
        HFT h = Hft();
        if (!h) return AIS_NOT_CONFIGURED;
        return ((AISSubmitProc)h[AISSel_Submit])(&req, done, clientData, job);
    }

    AISStatus Embed(const AISEmbedRequest& req, AISEmbedDoneProc done, void* clientData, AISJob* job)
    {
        HFT h = Hft();
        if (!h) return AIS_NOT_CONFIGURED;
        return ((AISEmbedProc)h[AISSel_Embed])(&req, done, clientData, job);
    }

    void Cancel(AISJob job)
    {
        HFT h = Hft();
        if (h && job) ((AISCancelProc)h[AISSel_Cancel])(job);
    }

    AISStatus ShowSettings(HWND owner)
    {
        HFT h = Hft();
        if (!h) return AIS_NOT_CONFIGURED;
        return ((AISShowSettingsProc)h[AISSel_ShowSettings])(owner);
    }

    // Zero-initialised requests with size, caller id and purpose filled in.
    static AISRequest MakeRequest(const char* pluginId, const char* purpose)
    {
        AISRequest r;
        memset(&r, 0, sizeof r);
        r.size = sizeof r;
        r.pluginId = pluginId;
        r.purpose = purpose;
        r.temperature = -1.0f;
        return r;
    }

    static AISEmbedRequest MakeEmbedRequest(const char* pluginId, const char* purpose)
    {
        AISEmbedRequest r;
        memset(&r, 0, sizeof r);
        r.size = sizeof r;
        r.pluginId = pluginId;
        r.purpose = purpose;
        return r;
    }

private:
    HFT Hft()
    {
        if (!m_hft)
            m_hft = DUExtensionMgrGetHFT(DUAtomFromString(AIS_HFT_NAME), AIS_HFT_VERSION_1);
        return m_hft;
    }
    HFT m_hft = NULL;
};
