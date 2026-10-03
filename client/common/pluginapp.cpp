/*********************************************************************

 PluginApp.cpp

 - Implementation of the CWinApp derived class.

*********************************************************************/

#include "StdAfx.h"
#include "PluginApp.h"

#ifdef _DEBUG
#define new DEBUG_NEW
#undef THIS_FILE
static char THIS_FILE[] = __FILE__;
#endif

/////////////////////////////////////////////////////////////////////////////
// CFeaturePackApp

BEGIN_MESSAGE_MAP(CFeaturePackApp, CWinApp)
END_MESSAGE_MAP()

/////////////////////////////////////////////////////////////////////////////
// CFeaturePackApp construction

CFeaturePackApp::CFeaturePackApp()
{
}

/////////////////////////////////////////////////////////////////////////////
// The one and only CFeaturePackApp object - required for MFC initialization

CFeaturePackApp theApp;

/////////////////////////////////////////////////////////////////////////////
// CFeaturePackApp Initialization

BOOL CFeaturePackApp::InitInstance()
{
    DisableThreadLibraryCalls(AfxGetInstanceHandle());
    return TRUE;
}
