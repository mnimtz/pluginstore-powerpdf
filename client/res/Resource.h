// Resource.h — Plugin-Store client for Tungsten Power PDF.
//
// Ranges:
//   100..199   strings (ribbon, dialog, options)
//   250..259   bitmaps
//   1460..1469 options page controls
//   1500..1519 store dialog controls
//
// Every IDS_ must exist in ALL language blocks of PluginStore.rc; English is
// the runtime fallback.

#pragma once

// ---- pack-wide ----
#define IDS_PS_TITLE            100     // options category: "Plugin-Store"
#define IDS_PS_TAB_TITLE        101     // shared ribbon tab, IDENTICAL to the Feature Pack title

// ---- ribbon ----
#define IDS_PS_GROUP            110
#define IDS_PS_BTN_OPEN         111
#define IDS_PS_TIP_OPEN         112

// ---- store dialog ----
#define IDS_PSD_TITLE           120
#define IDS_PSD_C_NAME          121
#define IDS_PSD_C_AVAIL         122
#define IDS_PSD_C_INSTALLED     123
#define IDS_PSD_C_STATUS        124
#define IDS_PSD_C_SIZE          125
#define IDS_PSD_BTN_INSTALL     126
#define IDS_PSD_BTN_REFRESH     127
#define IDS_PSD_BTN_CLOSE       128
#define IDS_PSD_ST_NOTINST      129
#define IDS_PSD_ST_INSTALLED    130
#define IDS_PSD_ST_UPDATE       131
#define IDS_PSD_MSG_FAIL        132
#define IDS_PSD_MSG_INSTOK      133
#define IDS_PSD_MSG_INSTFAIL    134
#define IDS_PSD_CONFIRM         135
#define IDS_PSD_ADMIN_NOTE      136
#define IDS_PSD_MSG_HASH        137
#define IDS_PSD_EMPTY           138
#define IDS_PSD_BETA_TAG        139
#define IDS_PSD_BTN_UNINSTALL   140
#define IDS_PSD_CONFIRM_UNINST  141
#define IDS_PSD_MSG_UNINSTOK    142
#define IDS_PSD_SELF_UPDATE     143
#define IDS_PSD_BTN_SELFUPD     144
#define IDS_PSD_MSG_SELFUPD     145
#define IDS_PSD_AUTHOR          146
#define IDS_PSD_ASK_RESTART     147
#define IDS_PSD_ASK_RESTART_UN  148
#define IDS_PSD_C_AUTHOR        149
#define IDS_PSD_DISCLAIMER      159
#define IDS_PSD_DISCLAIMER_FULL 160
#define IDS_PSD_BTN_DISCLAIMER  161
#define IDS_PSD_C_CONTACT       158

// ---- options page ----
#define IDS_PSO_PAGE            150
#define IDS_PSO_GRP             151
#define IDS_PSO_URL_LBL         152
#define IDS_PSO_BETA            153
#define IDS_PSO_VERSIONLBL      154
#define IDS_PSO_HINT            155
#define IDS_PSO_VERBOSE         156
#define IDS_PSO_GRP_DIAG        157

// ---- bitmaps ----
#define IDB_STORE               250
#define IDB_STORE16             251

// ---- options page dialog ----
#define IDD_PS_OPTIONS          1460
#define IDC_PSO_GRP             1461
#define IDC_PSO_URL_LBL         1462
#define IDC_PSO_URL             1463
#define IDC_PSO_BETA            1464
#define IDC_PSO_VERSION         1465
#define IDC_PSO_HINT            1466
#define IDC_PSO_GRP_DIAG        1467
#define IDC_PSO_VERBOSE         1468

// ---- store dialog ----
#define IDD_PS_DIALOG           1500
#define IDC_PS_LIST             1501
#define IDC_PS_DESC             1502
#define IDC_PS_REFRESH          1503
#define IDC_PS_INSTALL          1504
#define IDC_PS_STATUS           1505
#define IDC_PS_ADMIN_NOTE       1506
#define IDC_PS_UNINSTALL        1507
#define IDC_PS_SELFUPDATE       1508
#define IDC_PS_DISCLAIMER       1509
#define IDC_PS_DISCLAIMER_BTN   1510
