// Offline integration-test fixture. Never opens a tuner or network connection.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <climits>
#include <fstream>
#include <filesystem>
#include <string>
#include "../../EpgCollector-reference/BonCtrl/IBonDriver2.h"
namespace fs=std::filesystem;
HMODULE self;
class Mock final:public IBonDriver2 {
    std::ifstream input;BYTE buffer[188*256];DWORD channel=0,calls=0,switchAfter=0;std::wstring switchFile;
public:
    const BOOL OpenTuner() override{
        wchar_t path[32768];GetModuleFileNameW(self,path,32768);auto ini=fs::path(path).replace_extension(L".ini");
        if(GetPrivateProfileIntW(L"Replay",L"OpenHang",0,ini.c_str()))Sleep(120000);
        return !GetPrivateProfileIntW(L"Replay",L"OpenFail",0,ini.c_str());
    }
    void CloseTuner() override{input.close();}
    const BOOL SetChannel(BYTE c)override{return SetChannel(0,c);}
    const float GetSignalLevel()override{return 30;}
    const DWORD WaitTsStream(DWORD)override{return WAIT_OBJECT_0;}
    const DWORD GetReadyCount()override{return 1;}
    const BOOL GetTsStream(BYTE* dest,DWORD* size,DWORD* remain)override{BYTE* p;auto ok=GetTsStream(&p,size,remain);if(ok)memcpy(dest,p,*size);return ok;}
    const BOOL GetTsStream(BYTE** dest,DWORD* size,DWORD* remain)override{
        Sleep(20);*remain=0;*size=0;if(!input.is_open())return FALSE;
        if(switchAfter && ++calls==switchAfter){input.close();input.clear();input.open(fs::path(switchFile),std::ios::binary);}
        input.read((char*)buffer,sizeof(buffer));*size=(DWORD)input.gcount();*dest=buffer;
        if(!*size){input.clear();input.seekg(0);}return TRUE;
    }
    void PurgeTsStream()override{}
    void Release()override{delete this;}
    const BON16CHAR* GetTunerName()override{return L"Offline replay fixture";}
    const BOOL IsTunerOpening()override{return TRUE;}
    const BON16CHAR* EnumTuningSpace(DWORD s)override{return s?nullptr:L"Replay";}
    const BON16CHAR* EnumChannelName(DWORD s,DWORD c)override{return !s&&c<2?L"Replay TS":nullptr;}
    const BOOL SetChannel(DWORD s,DWORD c)override{
        if(s||c>1)return FALSE;
        wchar_t path[32768],value[32768];GetModuleFileNameW(self,path,32768);auto ini=fs::path(path).replace_extension(L".ini");
        GetPrivateProfileStringW(L"Replay",std::to_wstring(c).c_str(),L"",value,32768,ini.c_str());
        input.close();input.clear();input.open(fs::path(value),std::ios::binary);channel=c;
        switchAfter=GetPrivateProfileIntW(L"Replay",L"SwitchAfter",0,ini.c_str());calls=0;
        GetPrivateProfileStringW(L"Replay",L"SwitchFile",L"",value,32768,ini.c_str());switchFile=value;
        return input.is_open();
    }
    const DWORD GetCurSpace()override{return 0;}
    const DWORD GetCurChannel()override{return channel;}
};
extern "C" __declspec(dllexport) IBonDriver* CreateBonDriver(){return new Mock;}
BOOL WINAPI DllMain(HINSTANCE h,DWORD reason,LPVOID){if(reason==DLL_PROCESS_ATTACH)self=h;return TRUE;}
