#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <cstdio>
#include <filesystem>
#include <fstream>
#include <vector>
#include <string>
#include <sstream>
#include <stdexcept>
#include <algorithm>
#include <climits>
#include <cmath>
#include <memory>
#include "DropCounter.h"
#include "../../EpgCollector-reference/BonCtrl/IBonDriver2.h"
#include "../../EpgCollector-reference/Common/EpgDataCap3Def.h"
namespace fs = std::filesystem;

template<class T> T proc(HMODULE m, const char* n) {
    auto p = GetProcAddress(m,n); if(!p) throw std::runtime_error(std::string("Missing export: ")+n); return reinterpret_cast<T>(p);
}
struct Parser {
    HMODULE module{}; DWORD id{};
    DWORD(WINAPI *init)(BOOL,DWORD*); DWORD(WINAPI *uninit)(DWORD);
    DWORD(WINAPI *add)(DWORD,BYTE*,DWORD); DWORD(WINAPI *tsid)(DWORD,WORD*,WORD*);
    void(WINAPI *clear)(DWORD);
    EPG_SECTION_STATUS(WINAPI *status)(DWORD,WORD,WORD,WORD,BOOL);
    DWORD(WINAPI *services)(DWORD,DWORD*,SERVICE_INFO**);
    DWORD(WINAPI *events)(DWORD,WORD,WORD,WORD,DWORD*,EPG_EVENT_INFO**);
    Parser() {
        wchar_t path[32768]; GetModuleFileNameW(nullptr,path,32768);
        module=LoadLibraryExW((fs::path(path).parent_path()/L"EpgDataCap3.dll").c_str(),nullptr,LOAD_WITH_ALTERED_SEARCH_PATH);
        if(!module) throw std::runtime_error("Cannot load EpgDataCap3.dll");
        init=proc<decltype(init)>(module,"InitializeEP"); uninit=proc<decltype(uninit)>(module,"UnInitializeEP");
        add=proc<decltype(add)>(module,"AddTSPacketEP"); tsid=proc<decltype(tsid)>(module,"GetTSIDEP");
        clear=proc<decltype(clear)>(module,"ClearSectionStatusEP"); status=proc<decltype(status)>(module,"GetSectionStatusServiceEP");
        services=proc<decltype(services)>(module,"GetServiceListEpgDBEP"); events=proc<decltype(events)>(module,"GetEpgInfoListEP");
        if(init(FALSE,&id)!=1) throw std::runtime_error("EPG parser initialization failed");
    }
    ~Parser(){if(id) uninit(id); if(module) FreeLibrary(module);}
};
struct Driver {
    HMODULE module{}; IBonDriver* base{}; IBonDriver2* bon{}; CBonStruct2Adapter adapter; bool opened{};
    Driver(const fs::path& path) {
        SetDllDirectoryW(path.parent_path().c_str());
        module=LoadLibraryExW(path.c_str(),nullptr,LOAD_WITH_ALTERED_SEARCH_PATH);
        if(!module) throw std::runtime_error("BonDriver DLL load failed (architecture or dependency)");
        auto createStruct=reinterpret_cast<const STRUCT_IBONDRIVER*(*)()>(GetProcAddress(module,"CreateBonStruct"));
        if(createStruct){ auto s=createStruct(); if(s && adapter.Adapt(*s)) base=bon=&adapter; }
        else {
            base=proc<IBonDriver*(*)()>(module,"CreateBonDriver")();
            if(base) bon=dynamic_cast<IBonDriver2*>(base);
        }
        if(!bon) throw std::runtime_error("IBonDriver2 unavailable");
        if(!bon->OpenTuner()) throw std::runtime_error("OpenTuner failed: unavailable or occupied");
        opened=true;
    }
    ~Driver(){if(base){if(opened) base->CloseTuner();base->Release();}if(module)FreeLibrary(module);}
};
struct Target {WORD tsid,sid;};
struct Capture {
    DropCounter drops;
    Parser parser; std::ofstream file; int state=0; std::streampos totPos{}; uint64_t packets=0,eit=0; bool verified=false; ULONGLONG saveStarted=0;
    WORD onid,tsid; std::vector<Target> sids; std::vector<BYTE> pending;bool basic;
    Capture(WORD o,WORD t,std::vector<Target> s,const fs::path& out,bool b):file(out,std::ios::binary|std::ios::trunc),onid(o),tsid(t),sids(s),basic(b){if(!file)throw std::runtime_error("Cannot create staging file");}
    void packet(BYTE* p) {
        drops.add(p);
        if(p[0]!=0x47 || (p[1]&0x80) || (p[3]&0xC0) || !(p[3]&0x30))return;
        unsigned pid=((p[1]&31)<<8)|p[2]; if(pid>=0x30)return;
        parser.add(parser.id,p,188);
        WORD o=0,t=0;
        if(parser.tsid(parser.id,&o,&t)==1){
            if(o!=onid || t!=tsid){if(verified)throw std::runtime_error("Stream changed while collecting; output rejected");return;}
            if(!verified){verified=true;parser.clear(parser.id);}
        }
        if(!verified)return;
        if(pid==0 && (p[1]&0x40)) {
            if(state==0)state=1;
            else if(state==1){state=2;saveStarted=GetTickCount64();parser.clear(parser.id);BYTE n[188];std::fill_n(n,188,255);n[0]=0x47;n[1]=0x1F;n[2]=0xFF;n[3]=0x10;totPos=file.tellp();file.write((char*)n,188);}
        }
        if(pid==0x14 && state==2){state=3;auto pos=file.tellp();file.seekp(totPos);file.write((char*)p,188);file.seekp(pos);}
        else if((pid==0 && state>=1)||state>=2)file.write((char*)p,188);
        if(!file)throw std::runtime_error("Staging write failed");
        packets++; if(pid==0x12 || pid==0x26 || pid==0x27)eit++;
    }
    void feed(const BYTE* p,size_t n){
        pending.insert(pending.end(),p,p+n);size_t i=0;
        while(i+188<=pending.size()){
            if(pending[i]!=0x47 || (i+376<=pending.size() && pending[i+188]!=0x47)){i++;continue;}
            packet(pending.data()+i);i+=188;
        }
        pending.erase(pending.begin(),pending.begin()+i);
    }
    bool complete(int& done){
        WORD o=0,t=0;done=0;
        if(parser.tsid(parser.id,&o,&t)!=1)return false;
        if(o!=onid||t!=tsid){if(verified)throw std::runtime_error("Stream changed while collecting; output rejected");return false;}
        verified=true;
        int observed=0;
        for(auto s:sids){auto status=parser.status(parser.id,onid,s.tsid,s.sid,FALSE);if(status!=EpgNoData){observed++;if(status==EpgHEITAll||(basic&&status==EpgBasicAll))done++;}}
        return observed>0 && done==observed && eit>0 && state>=2;
    }
};
std::vector<Target> parseSids(const std::wstring& s,WORD tsid){std::wistringstream in(s);std::wstring a;std::vector<Target> result;while(std::getline(in,a,L',')){auto sep=a.find(L':');if(sep==std::wstring::npos)result.push_back({tsid,(WORD)std::stoul(a)});else result.push_back({(WORD)std::stoul(a.substr(0,sep)),(WORD)std::stoul(a.substr(sep+1))});}if(result.empty())throw std::runtime_error("No services selected");return result;}
int wmain(int argc,wchar_t**argv){
    try{
        if(argc==3 && (std::wstring(argv[1])==L"--inspect"||std::wstring(argv[1])==L"--inspect-events")){
            bool details=std::wstring(argv[1])==L"--inspect-events";
            Parser parser;std::ifstream in(fs::path(argv[2]),std::ios::binary);if(!in)throw std::runtime_error("Input missing");
            if(details){
                // Match EpgTimerSrv's load order: initial PAT, first TOT, then
                // all remaining packets, skipping that first TOT once.
                BYTE b[188];std::streampos seek=0;
                while(in.read((char*)b,188))if((((b[1]&31)<<8)|b[2])==0){if((b[1]&0x40)&&seek!=std::streampos(0))break;parser.add(parser.id,b,188);seek=in.tellg();}
                in.clear();in.seekg(seek);bool found=false;
                while(in.read((char*)b,188))if((((b[1]&31)<<8)|b[2])==0x14){parser.add(parser.id,b,188);found=true;break;}
                in.clear();in.seekg(seek);
                while(in.read((char*)b,188)){if(found&&(((b[1]&31)<<8)|b[2])==0x14){found=false;continue;}parser.add(parser.id,b,188);}
                in.clear();in.seekg(0);
            }
            BYTE p[188];uint64_t count=0;while(in.read((char*)p,188)){if(p[0]!=0x47)throw std::runtime_error("Invalid TS sync");if(!details)parser.add(parser.id,p,188);count++;}
            if(in.gcount()!=0)throw std::runtime_error("Truncated TS");
            DWORD n=0;SERVICE_INFO* list=nullptr;parser.services(parser.id,&n,&list);uint64_t events=0;
            for(DWORD i=0;i<n;i++){DWORD e=0;EPG_EVENT_INFO* items=nullptr;parser.events(parser.id,list[i].original_network_id,list[i].transport_stream_id,list[i].service_id,&e,&items);events+=e;}
            printf("{\"type\":\"inspect\",\"packets\":%llu,\"services\":%lu,\"events\":%llu",count,n,events);
            if(details){
                printf(",\"items\":[");bool first=true;
                for(DWORD i=0;i<n;i++){DWORD e=0;EPG_EVENT_INFO* items=nullptr;parser.events(parser.id,list[i].original_network_id,list[i].transport_stream_id,list[i].service_id,&e,&items);
                    for(DWORD j=0;j<e;j++){const auto& v=items[j];printf("%s{\"sid\":%u,\"id\":%u,\"year\":%u,\"month\":%u,\"day\":%u,\"hour\":%u,\"minute\":%u}",first?"":",",list[i].service_id,v.event_id,v.start_time.wYear,v.start_time.wMonth,v.start_time.wDay,v.start_time.wHour,v.start_time.wMinute);first=false;}}
                printf("]");
            }
            printf("}\n");return events?0:3;
        }
        // capture/replay input space channel ONID TSID timeout minimum SID-list staging-file cancel-file
        if(argc!=12 && argc!=13)throw std::runtime_error("Usage: --capture|--replay input space channel onid tsid timeout minimum sid-list stage cancel [--basic]");
        bool basic=argc==13&&std::wstring(argv[12])==L"--basic";if(argc==13&&!basic)throw std::runtime_error("Unknown option");
        bool replay=std::wstring(argv[1])==L"--replay";
        if(!replay && std::wstring(argv[1])!=L"--capture")throw std::runtime_error("Unknown mode");
        auto onid=(WORD)std::stoul(argv[5]),tsid=(WORD)std::stoul(argv[6]);
        unsigned timeout=std::stoul(argv[7]),minimum=std::stoul(argv[8]);
        if(timeout<=minimum || timeout>3600)throw std::runtime_error("Invalid time limits");
        Capture cap(onid,tsid,parseSids(argv[9],tsid),argv[10],basic);
        std::unique_ptr<Driver> driver;std::ifstream input;
        if(replay){input.open(fs::path(argv[2]),std::ios::binary);if(!input)throw std::runtime_error("Replay input missing");}
        else{driver=std::make_unique<Driver>(fs::absolute(argv[2]));if(!driver->bon->SetChannel(std::stoul(argv[3]),std::stoul(argv[4])))throw std::runtime_error("SetChannel failed");driver->bon->PurgeTsStream();printf("{\"type\":\"ready\"}\n");fflush(stdout);}
        // Give up early on a channel that never delivers the target stream or any EIT, instead of waiting for the full timeout.
        const ULONGLONG noDataLimit=60000;
        ULONGLONG start=GetTickCount64(),last=0,nextCompletionCheck=0,verifiedAt=0;bool eof=false;
        while(true){
            if(fs::exists(argv[11]))throw std::runtime_error("Cancelled");
            if(replay){BYTE b[188*256];input.read((char*)b,sizeof(b));auto n=input.gcount();if(n)cap.feed(b,(size_t)n);else eof=true;}
            else{BYTE* p=nullptr;DWORD n=0,remain=0;if(driver->bon->GetTsStream(&p,&n,&remain)&&p&&n)cap.feed(p,n);else Sleep(10);}
            auto now=GetTickCount64();int done=0;bool complete=cap.complete(done);
            if(now-last>=1000||eof){
                float signal=driver?driver->bon->GetSignalLevel():0;
                printf("{\"type\":\"progress\",\"seconds\":%llu,\"packets\":%llu,\"eit\":%llu,\"drop\":%llu,\"done\":%d,\"total\":%zu,\"signal\":%.3f}\n",(now-start)/1000,cap.packets,cap.eit,cap.drops.count,done,cap.sids.size(),std::isfinite(signal)?signal:0);fflush(stdout);last=now;
            }
            if(replay || (cap.saveStarted && now-cap.saveStarted>=minimum*1000ULL && now>=nextCompletionCheck)){
                nextCompletionCheck=now+10000;
                if(complete){cap.file.flush();if(!cap.file)throw std::runtime_error("Flush failed");printf("{\"type\":\"complete\"}\n");return 0;}
            }
            if(!replay){
                if(cap.verified&&!verifiedAt)verifiedAt=now;
                if(!cap.verified&&now-start>=noDataLimit)throw std::runtime_error("Target ONID/TSID not received within 60 seconds: existing dat preserved");
                if(verifiedAt&&cap.eit==0&&now-verifiedAt>=noDataLimit)throw std::runtime_error("No EIT received within 60 seconds: existing dat preserved");
            }
            if(eof || now-start>=timeout*1000ULL)throw std::runtime_error("Incomplete EPG: existing dat preserved");
        }
    }catch(const std::exception& e){fprintf(stderr,"%s\n",e.what());return 2;}
}
