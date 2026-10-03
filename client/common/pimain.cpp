
#include "stdafx.h"
#include "environ.h"

#include "picommon.h"
#include "pimain.h"

#include "ducalls.h"
#include "duextracalls.h"
#include "dvcalls.h"
#include "dcorecalls.h"
#include "dabcalls.h"
#include "pgcntcalls.h"
#include "ddcalls.h"
#include "dercalls.h"
#include "dewcalls.h"
#include "dsfcalls.h"
#include "rvcalls.h"
#include "ddswritecalls.h"
#include "ddsreadcalls.h"
#include "ddmetadatacalls.h"
#include "PIRequire.h"


/* This is for testing only.  Leave it set of 0xFFFFFFFF for final version */
#define TEST_OLD_VERSION 0xFFFFFFFF /*set lower to test old versions.*/


HFT gCoreHFT = 0;
DUUns32 gCoreVersion = 0;

#if DEBUG
/* For special DURING/HANDLER catching */
int gBadReturnCatcher;
#endif

HFT gDocuUtilitiesHFT;
DUUns32 gDocuUtilitiesVersion =0;

#if PI_RVIEW_VERSION != 0
HFT gRViewHFT = 0;
DUUns32 gRViewVersion = 0;
#endif

#if PI_DAB_VERSION != 0
HFT gDabHFT = 0;
DUUns32 gDabVersion = 0;
#endif

#if PI_DDMODEL_VERSION != 0
HFT gDDModelHFT = 0;
DUUns32 gDDModelVersion = 0;
#endif

#if PI_DDEEDIT_READ_VERSION != 0
HFT gDDEEditReadHFT = 0;
DUUns32 gDDEEditReadVersion = 0;
#endif

#if PI_DDEEDIT_WRITE_VERSION != 0
HFT gDDEEditWriteHFT = 0;
DUUns32 gDDEEditWriteVersion = 0;
#endif

#if PI_DDSYSFONT_VERSION != 0
HFT gDDSysFontHFT = 0;
DUUns32 gDDSysFontVersion = 0;
#endif

#if PI_PAGE_DDE_CONTENT_VERSION != 0
HFT gPageDDEContentHFT = 0;
DUUns32 gPageDDEContentVersion = 0;
#endif

#if PI_DOCUVIEW_VERSION != 0
HFT gDocuViewHFT = 0;
DUUns32 gDocuViewVersion = 0;
#endif

#if PI_DDSEDIT_WRITE_VERSION != 0
HFT gDDSWriteHFT = 0;
DUUns32 gDDSWriteVersion = 0;
#endif

#if PI_DDSEDIT_READ_VERSION != 0
HFT gDDSReadHFT = 0;
DUUns32 gDDSReadVersion = 0;
#endif

#if PI_WIN_VERSION != 0
HFT gWinHFT = 0;
DUUns32 gWinVersion = 0;
#endif

#if PI_DUEXTRA_VERSION != 0
HFT gDUExtraHFT = 0;
DUUns32 gDUExtraVersion = 0;
#endif

#if PI_DDMETADATA_VERSION != 0
HFT gDDMetadataHFT = 0;
DUUns32 gDDMetadataVersion = 0;
#endif

ExtensionID gExtensionID;		/* A identifying cookie sometimes needed by the application */

#include <windows.h>

HINSTANCE 	gHINSTANCE;
HWND 		gHWND;

#if (!_USRDLL && !_AFXDLL  && !CUSTOM_DLLMAIN)  /* Omit DllMain for MFC plug-ins and plug-ins with custom DllMain */

/***************************************************************************************************
The DllMain Function

Unlike Windows 3.x DLLs, Windows NT calls one function, DllMain, for both initialization and 
termination. It also makes calls on both a per-process and per-thread basis, so several initialization 
calls can be made if a process is multithreaded. 

DllMain uses the WINAPI convention and three parameters. The function returns 
TRUE (1) to indicate success. 

The lpReserved parameter is reserved for the system's use and should not 
be manipulated .

Win32 DLL initialization functions are passed the following information:

	The hModule parameter, a module handle for the dll
	The ul_reason_for_call parameter, an enumerated type that indicates which 
	of four reasons the LibMain procedure is being called: 
		process attach, 
		thread attach, 
		thread detach, 
		or process detach.

	The lpReserved parameter, which is unused.

	All calls to local memory management functions operate on the default heap.
	The command line can be obtained from GetCommandLine API function.
********************************************************************/                                                                          


BOOL APIENTRY DllMain( HANDLE hModule, 
                        DWORD ul_reason_for_call, 
                        LPVOID lpReserved )
{
    switch( ul_reason_for_call ) {

    case DLL_PROCESS_ATTACH: //A new process is attempting to access the DLL; one thread is assumed.
		DisableThreadLibraryCalls( (HMODULE) hModule); //we do not use the Thread_Attach or Thread_detach cases
		gHINSTANCE = (HINSTANCE)hModule;

	break;	
    case DLL_THREAD_ATTACH://A new thread of an existing process is attempting to access the DLL; this call is made beginning with the second thread of a process attaching to the DLL.
    	break;
    case DLL_THREAD_DETACH://One of the additional threads (not the first thread) of a process is detaching from the DLL.
	break;
    
    case DLL_PROCESS_DETACH://A process is detaching from the DLL.
    	break;
    }
    return TRUE;
}

#endif /* custom DllMain */


/* This struct contains platform specific data useful to plug-ins.  This structure will grow over time,
** always support the old structure and only add to the end.
*/

typedef struct V0200_DATA_t_ {
	HWND 		hWnd;
	HINSTANCE	hInstance;
} V0200_DATA;

DUBool CALLBACK PlugInMain(DUInt32 appHandshakeVersion,
													DUInt32 *handshakeVersion,
													PISetupSDKProcType* setupProc,
													void* windowsData)
{
	V0200_DATA* dataPtr = (V0200_DATA*) windowsData;
	gHWND = dataPtr->hWnd;
	gHINSTANCE = dataPtr->hInstance;

	/*
	** appsHandshakeVersion tells us which version of the handshake struct the application has sent us.
	** HANDSHAKE_VERSION is the latest version that we, the plug-in, know about (see PIVersn.h)
	** Always use the earlier of the two structs to assure compatibility.
	** The version we want to use is returned to the application so it can adjust accordingly.
	*/
	*handshakeVersion = (appHandshakeVersion < HANDSHAKE_VERSION) ? appHandshakeVersion : HANDSHAKE_VERSION;

	/* Provide the routine for the host app to call to setup this plug-in */
	*setupProc = PISetupSDK;

	return true;
}

DCCB1 void DCCB2 RestorePlugInFrame(void *asEnviron)
{
	DOCUlongjmp(*(DOCUjmp_buf*)asEnviron, 1);
}

/* pass in name of hft and minimum required version.  
   returns hft and version of the returned hft (>= requiredVer) and true if successful
   on failure, both resultHFT and resultingVer return as NULL
*/
static DUBool GetRequestedHFT(char* table, DUUns32 requiredVer, DUUns32 *resultingVer, HFT *resultHFT)
{
	DUAtom tablename = DUAtomFromString(table);
	DUVersion resultVer = HFT_ERROR_NO_VERSION;
	
	static DUUns32 versionLessVersions[] = {0x00050001, 0x00050000, 0x00040005, 0x00040000, 0x00020003, 0x00020002, 0x00020001};
	static DUUns32 kNUMVERSIONS = sizeof(versionLessVersions) / sizeof(DUUns32);

	DUUns32 i;
	HFT thft = NULL; /* we use a temp hft in case we are replacing gCoreHFT */

	if (gDocuUtilitiesVersion >= DuCallsHFT_VERSION_6) {
		thft = DUExtensionMgrGetHFT(tablename, requiredVer); 
		if (thft) {
			resultVer = HFTGetVersion(thft);
			DOCUASSERT(resultVer != HFT_ERROR_NO_VERSION); /* all HFTs support HFTGetVersion */
		}
	}
	if (resultVer == HFT_ERROR_NO_VERSION){
		/* without a GetVersion version of the HFT, we must try all versions from latest on down and see what we get */
		for (i=0;i<kNUMVERSIONS;i++) {
			if (versionLessVersions[i]<requiredVer)
				break;
			thft = DUExtensionMgrGetHFT(tablename, versionLessVersions[i]);
			if (thft)
				break;
		}
		if (thft) 
			resultVer = versionLessVersions[i];
		else 
			resultVer = 0;
	}
	*resultHFT = thft;
	*resultingVer = resultVer;
	if (TEST_OLD_VERSION < 0xFFFFFFFF && *resultingVer > TEST_OLD_VERSION)
		*resultingVer = TEST_OLD_VERSION;

	return *resultingVer != 0;
}

/* 
** This routine is called by the host application to set up the plug-in's SDK-provided functionality.
*/
DCCB1 DUBool DCCB2 PISetupSDK (DUUns32 handshakeVersion, void *sdkData)
{
	DUBool bSuccess;

	if (handshakeVersion == HANDSHAKE_V0200)
	{
		/* Cast sdkData to the appropriate type */
		PISDKData_V0200 *data = (PISDKData_V0200 *)sdkData;
		
		/* Belt and suspenders sanity check */
		if (data->handshakeVersion != HANDSHAKE_V0200)
			return false;

		/* Get our globals out */
		gExtensionID = data->extensionID;
		gCoreHFT = data->coreHFT;
		gCoreVersion = 0x00020000; /* lowest version that supports v0200 handshake */

		/*
		** Note that we just got the Core HFT, so now we can, and are expected to, DUCallbackCreate()
		** every function we pass back to the viewer.
		** We can now also call functions in the Core HFT (level 2), such as DUExtensionMgrGetHFT().
		*/
		/* Get the HFTs we want */
		/* this file wants DocuSupport for the HFTGetVersion call so try to get it if possible */
		gDocuUtilitiesHFT = DUExtensionMgrGetHFT(DUAtomFromString("DocuSupport"), DuCallsHFT_VERSION_6);
		if (gDocuUtilitiesHFT) {
			gDocuUtilitiesVersion = DuCallsHFT_VERSION_6;
			gDocuUtilitiesVersion = HFTGetVersion(gDocuUtilitiesHFT);
			bSuccess = true;
#if	PI_DOCUSUPPORT_VERSION != 0
			/* got version 6, now check to see if docusupport is also compatible with rest of plugin */
			bSuccess = GetRequestedHFT("DocuSupport",PI_DocuSUPPORT_VERSION,&gDocuUtilitiesVersion,&gDocuUtilitiesHFT);
#endif
		}
		else {
#if	PI_DOCUSUPPORT_VERSION == 0
			bSuccess = true;
#else
			bSuccess = GetRequestedHFT("DocuSupport",PI_DOCUSUPPORT_VERSION,&gDocuUtilitiesVersion,&gDocuUtilitiesHFT);
#endif
		}
#ifndef PI_DOCUSUPPORT_OPTIONAL
		if (!bSuccess)
			return false;
#endif

		/* we'll rerequest the CoreHFT at the plugin's required level */
#if PI_CORE_VERSION == 0
#error Define PI_CORE_VERSION to 0x00020000 or over for core HFT.  This is not an optional HFT
#else
		bSuccess = GetRequestedHFT("Core",PI_CORE_VERSION,&gCoreVersion,&gCoreHFT);
		if (!bSuccess)
			return false;
#endif


#if PI_RVIEW_VERSION != 0
		bSuccess = GetRequestedHFT("RView",PI_RVIEW_VERSION,&gRViewVersion,&gRViewHFT);
#ifndef PI_RVIEW_OPTIONAL
		if (!bSuccess)
			return false;
#endif
#endif /* PI_RVIEW_VERSION  */

#if PI_DAB_VERSION != 0
		bSuccess = GetRequestedHFT("Dab", PI_DAB_VERSION,&gDabVersion,&gDabHFT);
#ifndef PI_DAB_OPTIONAL
		if (!bSuccess)
			return false;
#endif
#endif /* PI_DAB_VERSION  */

#if PI_DDMODEL_VERSION != 0
		bSuccess = GetRequestedHFT("DDModel",PI_DDMODEL_VERSION,&gDDModelVersion,&gDDModelHFT);
#ifndef PI_DDMODEL_OPTIONAL
		if (!bSuccess)
			return false;
#endif
#endif /* PI_DDMODEL_VERSION  */


#if PI_DDEEDIT_WRITE_VERSION != 0
		bSuccess = GetRequestedHFT(DDEEditWriteHFTName,PI_DDEEDIT_WRITE_VERSION,&gDDEEditWriteVersion,&gDDEEditWriteHFT);
#ifndef PI_DDEEDIT_WRITE_OPTIONAL
		if (!bSuccess)
			return false;
#endif
#endif /* PI_DDEEDIT_WRITE_VERSION  */

#if PI_DDEEDIT_READ_VERSION != 0
		bSuccess = GetRequestedHFT(DDEEditReadHFTName,PI_DDEEDIT_READ_VERSION,&gDDEEditReadVersion,&gDDEEditReadHFT);
#ifndef PI_DDEEDIT_READ_OPTIONAL
		if (!bSuccess)
			return false;
#endif
#endif /* PI_DDEEDIT_READ_VERSION  */

#if PI_DDSYSFONT_VERSION != 0
		bSuccess = GetRequestedHFT(DDSysFontHFTName,PI_DDSYSFONT_VERSION,&gDDSysFontVersion,&gDDSysFontHFT);
#ifndef PI_DDSYSFONT_OPTIONAL
		if (!bSuccess)
			return false;
#endif
#endif /* PI_DDSYSFONT_VERSION  */

#if PI_PAGE_DDE_CONTENT_VERSION != 0
		bSuccess = GetRequestedHFT(PageDDEContentHFTName,PI_PAGE_DDE_CONTENT_VERSION,&gPageDDEContentVersion,&gPageDDEContentHFT);
#ifndef PI_PAGE_DDE_CONTENT_OPTIONAL
		if (!bSuccess)
			return false;
#endif
#endif /* PI_PAGE_DDE_CONTENT_VERSION  */

#if PI_DOCUVIEW_VERSION != 0
		bSuccess = GetRequestedHFT("DocuView",PI_DOCUVIEW_VERSION,&gDocuViewVersion,&gDocuViewHFT);
#ifndef PI_DOCUVIEW_OPTIONAL
		if (!bSuccess)
			return false;
#endif
#endif /* PI_DOCUVIEW_VERSION  */

#if PI_DDSEDIT_WRITE_VERSION != 0
		bSuccess = GetRequestedHFT(DDSWriteHFTName,PI_DDSEDIT_WRITE_VERSION,&gDDSWriteVersion,&gDDSWriteHFT);
#ifndef PI_DDSEDIT_WRITE_OPTIONAL
		if (!bSuccess)
			return false;
#endif
#endif /* PI_DDSEDIT_WRITE_VERSION  */

#if PI_DDSEDIT_READ_VERSION != 0
		bSuccess = GetRequestedHFT(DDSReadHFTName,PI_DDSEDIT_READ_VERSION,&gDDSReadVersion,&gDDSReadHFT);
#ifndef PI_DDSEDIT_READ_OPTIONAL
		if (!bSuccess)
			return false;
#endif
#endif /* PI_DDSEDIT_READ_VERSION  */


#if PI_WIN_VERSION != 0
		bSuccess = GetRequestedHFT("Win",PI_WIN_VERSION,&gWinVersion,&gWinHFT);
#ifndef PI_WIN_OPTIONAL
		if (!bSuccess)
			return false;
#endif
#endif /* PI_WIN_VERSION  */

#if PI_DUEXTRA_VERSION != 0
		bSuccess = GetRequestedHFT("DUExtra",PI_DUEXTRA_VERSION,&gDUExtraVersion,&gDUExtraHFT);
#ifndef PI_DUEXTRA_OPTIONAL
		if (!bSuccess)
			return false;
#endif
#endif /* PI_DUEXTRA_VERSION  */


#if PI_DDMETADATA_VERSION != 0
		bSuccess = GetRequestedHFT(DDMetadataHFTName,PI_DDMETADATA_VERSION,&gDDMetadataVersion,&gDDMetadataHFT);
#ifndef PI_DDMETADATA_OPTIONAL
		if (!bSuccess)
			return false;
#endif
#endif /* PI_PDMETADATA_VERSION  */


		/* Set the plug-in's handshake routine, which is called next by the host application */
		data->handshakeCallback = DUCallbackCreateProto(PIHandshakeProcType, PIHandshake);

		/* Return success */
		return true;

	} /* Each time the handshake version changes, add a new "else if" branch here */

	/* 
	** If we reach here, then we were passed a handshake version number we don't know about.
	** This shouldn't ever happen since our main() routine chose the version number.
	*/
	return false;
}

