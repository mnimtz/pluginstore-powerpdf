/*
 * aiservices.h - public interface of the AI Plug-in (KI-Plugin, Tungsten AI
 * Services, store package com.tungsten.aiservices) for Power PDF plug-ins.
 *
 * MIT License. Copyright (c) 2026 Tungsten Automation.
 * Permission is hereby granted, free of charge, to any person obtaining a copy of this file,
 * to deal in it without restriction, including the rights to use, copy, modify, merge,
 * publish, distribute, sublicense and/or sell copies, subject to this notice being kept.
 * THE FILE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND.
 * Download: https://addon.power-pdf.de/api/sdk/aiservices.h (guide: /api/agent-guide,
 * section "Using the AI Plug-in from your add-on").
 *
 * The AI Plug-in publishes a host function table (HFT) named AIS_HFT_NAME.
 * Other plug-ins import it with DUExtensionMgrGetHFT and send AI requests
 * through the provider the customer configured once (OpenAI, Google Gemini,
 * Anthropic Claude, OpenAI-compatible services, local Ollama). Plug-ins never
 * see API keys.
 *
 * What version 1 offers
 *   - chat requests: one question or a whole conversation, with attachments
 *     (JPEG, PNG, PDF), optional JSON Schema for structured answers
 *   - model classes: default, fast, thorough (mapped to models by the admin)
 *   - embeddings: texts -> vectors (similarity, classification, search)
 *   - optional partial-answer callback (streaming)
 *   - capabilities incl. context window, so callers can plan request sizes
 *
 * Version 1 is frozen. Later versions only append selectors and append fields
 * to the end of structures (every top-level structure starts with `size`).
 *
 * Rules for callers
 *   - Call every function on the UI thread only.
 *   - Look the HFT up lazily (on first use), not in PluginInit: plug-in start
 *     order is not guaranteed. NULL means "KI-Plugin not installed" -> hide or
 *     disable AI options and keep working without AI.
 *   - Do not submit requests during start-up; wait for a user action.
 *   - Strings are UTF-8, NUL-terminated.
 *   - Every AISSubmit / AISEmbed that returns AIS_OK calls its done callback
 *     exactly once, on the UI thread, also after AISCancel (AIS_CANCELLED).
 *     Only exception: Power PDF is shutting down.
 *   - Pointers inside response structures are valid only during the callback.
 *
 * Plain C types only: caller and KI-Plugin are separately built DLLs.
 */
#ifndef TUNGSTEN_AISERVICES_H
#define TUNGSTEN_AISERVICES_H

#include <stdint.h>
#include <windows.h>

#ifdef __cplusplus
extern "C" {
#endif

#define AIS_HFT_NAME        "TungstenAIServices"
#define AIS_HFT_VERSION_1   0x00010000u
#define AIS_HFT_VERSION     AIS_HFT_VERSION_1

#define AISAPI __cdecl

/* ---- status codes ------------------------------------------------------ */
typedef int32_t AISStatus;
#define AIS_OK                 0
#define AIS_DISABLED           1   /* AI switched off (user, policy or Copilot switch) */
#define AIS_NOT_CONFIGURED     2   /* no provider, model or key set up                 */
#define AIS_NOT_PERMITTED      3   /* this plug-in is not allowed to use AI            */
#define AIS_BUSY               4   /* too many requests running, try again later       */
#define AIS_CANCELLED          5
#define AIS_RATE_LIMITED       6   /* provider quota or rate limit                     */
#define AIS_AUTH_FAILED        7   /* key rejected                                     */
#define AIS_PROVIDER_ERROR     8   /* provider answered with an error                  */
#define AIS_NETWORK_ERROR      9   /* no connection, TLS, proxy, timeout               */
#define AIS_BAD_RESPONSE      10   /* answer missing or not valid (e.g. not JSON)      */
#define AIS_BLOCKED           11   /* provider safety filter blocked the content       */
#define AIS_INVALID_ARGUMENT  12   /* programming error: missing field, wrong thread   */
#define AIS_INTERNAL_ERROR    13
#define AIS_NOT_SUPPORTED     14   /* configured model cannot do this (PDF, embeddings)*/

/* ---- model classes ------------------------------------------------------ */
#define AIS_MODEL_DEFAULT    0     /* the configured standard model                   */
#define AIS_MODEL_FAST       1     /* cheap and quick, e.g. for many small requests   */
#define AIS_MODEL_THOROUGH   2     /* best quality, slower and more expensive         */
/* Without an admin mapping every class uses the standard model. */

/* ---- capabilities (no secrets) ---------------------------------------- */
#define AIS_CAP_ENABLED      0x0001u   /* AI is switched on                         */
#define AIS_CAP_CONFIGURED   0x0002u   /* provider + model (+ key) present          */
#define AIS_CAP_PERMITTED    0x0004u   /* calling plug-in may use AI                */
#define AIS_CAP_VISION       0x0008u   /* JPEG/PNG attachments accepted             */
#define AIS_CAP_JSON_SCHEMA  0x0010u   /* structured output supported               */
#define AIS_CAP_LOCAL        0x0020u   /* data stays on this machine (Ollama)       */
#define AIS_CAP_PDF          0x0040u   /* PDF attachments accepted                  */
#define AIS_CAP_EMBEDDINGS   0x0080u   /* AISEmbed available                        */
#define AIS_CAP_STREAMING    0x0100u   /* partial callback delivers real increments */

typedef struct AISCaps {
    uint32_t size;               /* = sizeof(AISCaps), set by the caller          */
    uint32_t flags;              /* AIS_CAP_*                                     */
    char     provider[32];       /* "openai" | "gemini" | "ollama" | "anthropic" | "compatible" | "" (0.6.0) */
    char     model[128];         /* model behind the requested class              */
    uint32_t contextTokens;      /* input limit of that model, 0 = unknown        */
    uint32_t maxOutputTokens;    /* output limit of that model, 0 = unknown       */
    char     embeddingModel[128];
} AISCaps;

/* ---- chat request ------------------------------------------------------ */
typedef struct AISAttachment {
    const char* mimeType;        /* "image/jpeg", "image/png", "application/pdf"  */
    const void* data;
    uint32_t    length;          /* bytes                                         */
} AISAttachment;

#define AIS_ROLE_USER        1
#define AIS_ROLE_ASSISTANT   2

typedef struct AISMessage {
    uint32_t             role;           /* AIS_ROLE_*                            */
    const char*          text;
    const AISAttachment* attachments;    /* user messages only                    */
    uint32_t             attachmentCount;
} AISMessage;

typedef uint64_t AISJob;                 /* 0 = no job */

/* Partial answer while it is being generated (UI thread). `delta` is the new
   piece, `textSoFar` everything up to now. May be called zero or more times;
   the complete answer always arrives in the done callback. */
typedef void (AISAPI *AISPartialProc)(AISJob job, const char* delta,
                                      const char* textSoFar, void* clientData);

#define AIS_REQ_STRICT_SCHEMA  0x0001u   /* enforce jsonSchema strictly (OpenAI)  */

typedef struct AISRequest {
    uint32_t             size;            /* = sizeof(AISRequest)                    */
    const char*          pluginId;        /* Store add-on id of the caller           */
    const char*          purpose;         /* short reason, shown to user and admin   */
    const char*          system;          /* optional system instruction             */

    /* Conversation so far (optional), oldest first. */
    const AISMessage*    messages;
    uint32_t             messageCount;

    /* Short form for the (last) user turn, appended after `messages`. */
    const char*          user;
    const AISAttachment* attachments;
    uint32_t             attachmentCount;

    const char*          jsonSchema;      /* optional JSON Schema of the answer      */
    const char*          schemaName;      /* optional, e.g. "change_summary"         */
    uint32_t             maxOutputTokens; /* visible answer, 0 = default (2048)      */
    float                temperature;     /* < 0 = provider default                  */
    uint32_t             flags;           /* AIS_REQ_*                               */
    uint32_t             timeoutSeconds;  /* 0 = default (300)                       */
    uint32_t             modelClass;      /* AIS_MODEL_*                             */
    AISPartialProc       partial;         /* optional, see AISPartialProc            */
} AISRequest;

typedef struct AISResponse {
    uint32_t    size;
    AISStatus   status;
    const char* text;          /* answer (JSON text if jsonSchema was given)   */
    const char* error;         /* user-readable, never contains the key        */
    uint32_t    inputTokens;
    uint32_t    outputTokens;
    const char* provider;
    const char* model;
} AISResponse;

typedef void (AISAPI *AISDoneProc)(const AISResponse* response, void* clientData);

/* ---- embeddings -------------------------------------------------------- */
#define AIS_EMBED_DOCUMENT   0u   /* texts to be searched / compared (default)   */
#define AIS_EMBED_QUERY      1u   /* search queries                              */

typedef struct AISEmbedRequest {
    uint32_t           size;           /* = sizeof(AISEmbedRequest)               */
    const char*        pluginId;
    const char*        purpose;
    const char* const* texts;          /* UTF-8 texts                             */
    uint32_t           textCount;      /* 1 .. 4096                               */
    uint32_t           taskType;       /* AIS_EMBED_*                             */
    uint32_t           timeoutSeconds; /* 0 = default (300)                       */
} AISEmbedRequest;

typedef struct AISEmbedResponse {
    uint32_t     size;
    AISStatus    status;
    const char*  error;
    uint32_t     count;        /* number of vectors (= textCount on success)   */
    uint32_t     dimensions;
    const float* vectors;      /* count * dimensions floats, one row per text  */
    uint32_t     inputTokens;
    const char*  provider;
    const char*  model;
} AISEmbedResponse;

typedef void (AISAPI *AISEmbedDoneProc)(const AISEmbedResponse* response, void* clientData);

/* ---- functions (HFT selectors) ---------------------------------------- */
enum {
    AISSel_GetCaps = 1,
    AISSel_RequestPermission,
    AISSel_Submit,
    AISSel_Cancel,
    AISSel_ShowSettings,
    AISSel_Embed,
    AISSel_CountV1             /* number of selectors in version 1, incl. 0  */
};

/* What is available for this plug-in and model class. Cheap, no network. */
typedef AISStatus (AISAPI *AISGetCapsProc)(const char* pluginId, uint32_t modelClass,
                                           AISCaps* caps);

/* Asks for permission to use AI. May show a consent dialog owned by `owner`.
   Returns AIS_OK when allowed (also when already allowed earlier). */
typedef AISStatus (AISAPI *AISRequestPermissionProc)(const char* pluginId,
                                                     const char* pluginName,
                                                     const char* purpose,
                                                     HWND owner);

/* Starts a chat request in the background. On AIS_OK `*job` identifies it
   and `done` follows on the UI thread. */
typedef AISStatus (AISAPI *AISSubmitProc)(const AISRequest* request,
                                          AISDoneProc done, void* clientData,
                                          AISJob* job);

/* Cancels a running chat or embedding job; its callback follows with AIS_CANCELLED. */
typedef void      (AISAPI *AISCancelProc)(AISJob job);

/* Opens the central AI settings page. */
typedef AISStatus (AISAPI *AISShowSettingsProc)(HWND owner);

/* Starts an embedding request in the background. */
typedef AISStatus (AISAPI *AISEmbedProc)(const AISEmbedRequest* request,
                                         AISEmbedDoneProc done, void* clientData,
                                         AISJob* job);

#ifdef __cplusplus
}
#endif
#endif /* TUNGSTEN_AISERVICES_H */
