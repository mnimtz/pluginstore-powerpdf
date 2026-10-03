#if _DEBUG
#define DEBUG 1
#endif
#define WINVER 0x0601

#pragma warning (disable:4800)

#include <afxwin.h>         // MFC base classes
#include <afxext.h>         // MFC extensions
#include <afxtempl.h>
#include <afxcmn.h>
#include <locale.h>
#include <atlstr.h>
#include <afxstr.h>

#include <vector>
#include <string>
#include <sstream>
#include <map>
#include <algorithm>
#include <ctime>
#include <cwchar>

#ifdef _AFXDLL
#   define AFX_MANAGE_MODULE_STATE AFX_MANAGE_STATE(AfxGetStaticModuleState())
#else
#   define AFX_MANAGE_MODULE_STATE
#endif

// SDK surface used by the pack. Kept as the proven v1 union so a new module
// never starts by fighting missing HFTs:
//   DDCalls (documents, pages, save), DabCalls (COS objects),
//   DERCalls (text extraction), DEWCalls (content streams), PgCntCalls
#include "PIHeaders.h"
#include "RVCalls.h"
#include "WinCalls.h"
#include "DDCalls.h"
#include "DabCalls.h"
#include "DERCalls.h"
#include "DEWCalls.h"
#include "PgCntCalls.h"
#include "Resource.h"
