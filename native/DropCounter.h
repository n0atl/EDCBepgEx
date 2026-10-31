#pragma once
#include <array>
#include <cstdint>

// Continuity rules follow EDCB BonCtrl/DropCount.cpp (including one duplicate).
struct DropCounter {
    struct Pid { int last=-1; bool duplicate=false; };
    std::array<Pid,8192> pids{};
    uint64_t count=0;
    void add(const unsigned char* p) {
        if(p[0]!=0x47 || (p[1]&0x80))return;
        unsigned pid=((p[1]&31)<<8)|p[2];if(pid==0x1FFF)return;
        auto& s=pids[pid];int af=(p[3]>>4)&3,cc=p[3]&15;
        bool discontinuity=(af&2)&&p[4]>0&&(p[5]&0x80);
        if(af==0)s.duplicate=false;
        else if(af==2){if(s.last>=0&&s.last!=cc&&!discontinuity)count++;s.duplicate=false;}
        else if(s.last==cc){if(!discontinuity){if(s.duplicate)count++;else s.duplicate=true;}else s.duplicate=false;}
        else {if(s.last>=0&&((s.last+1)&15)!=cc&&!discontinuity)count++;s.duplicate=false;}
        s.last=cc;
    }
};
