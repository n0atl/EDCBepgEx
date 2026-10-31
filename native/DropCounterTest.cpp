#include "DropCounter.h"
#include <cassert>
#include <cstdio>
int main(){
    DropCounter d;unsigned char p[188]={0x47,0,0x12,0x10};
    d.add(p);p[3]=0x11;d.add(p);assert(d.count==0);
    p[3]=0x14;d.add(p);assert(d.count==1);
    d.add(p);assert(d.count==1);d.add(p);assert(d.count==2);
    p[3]=0x3A;p[4]=1;p[5]=0x80;d.add(p);assert(d.count==2);
    p[3]=0x1B;d.add(p);assert(d.count==2);
    p[1]=0x1F;p[2]=0xFF;p[3]=0x10;d.add(p);p[3]=0x15;d.add(p);assert(d.count==2);
    p[1]=0x80;p[2]=0x12;d.add(p);assert(d.count==2);
    p[1]=0;p[2]=0x12;p[3]=0x2B;p[4]=0;d.add(p);assert(d.count==2);
    p[3]=0x2C;d.add(p);assert(d.count==3);
    puts("PASS continuity, gaps, duplicate, discontinuity, null, TEI, adaptation-only");
}
