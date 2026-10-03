/*********************************************************************

 PluginApp.h

 - Interface of the CWinApp derived class required for MFC.

*********************************************************************/

#if !defined(AFX_PLUGINAPP_H)
#define AFX_PLUGINAPP_H

#if _MSC_VER > 1000
#pragma once
#endif // _MSC_VER > 1000

#ifndef __AFXWIN_H__
    #error include 'stdafx.h' before including this file for PCH
#endif

#include "resource.h"       // main symbols

/////////////////////////////////////////////////////////////////////////////
// CFeaturePackApp:
// See PluginApp.cpp for the implementation of this class
//

class CFeaturePackApp : public CWinApp
{
public:
    CFeaturePackApp();

// Overrides
    //{{AFX_VIRTUAL(CFeaturePackApp)
    //}}AFX_VIRTUAL

// Implementation
    //{{AFX_MSG(CFeaturePackApp)
    //}}AFX_MSG
    DECLARE_MESSAGE_MAP()
    virtual BOOL InitInstance();
};


/////////////////////////////////////////////////////////////////////////////

//{{AFX_INSERT_LOCATION}}
// Microsoft Visual C++ will insert additional declarations immediately before the previous line.

#endif // !defined(AFX_PLUGINAPP_H)
