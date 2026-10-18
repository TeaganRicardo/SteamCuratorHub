// Test double for exercising the helper's actual dlopen/vtable/callback path.
// This file never accesses a real Steam installation, account or network.
#import <Foundation/Foundation.h>
#include <dlfcn.h>
#include <cstdint>
#include <cstring>
#include <deque>
#include <vector>
#include <cstdlib>

struct Object { void **vtable; };
struct Message { int user; int id; const uint8_t *data; int size; };
struct Event { int id; std::vector<uint8_t> bytes; };
static void *clientSlots[16]{}, *userSlots[3]{}, *utilsSlots[14]{}, *appsSlots[7]{}, *statsSlots[37]{};
static Object client{clientSlots}, user{userSlots}, utils{utilsSlots}, apps{appsSlots}, stats{statsSlots};
static std::deque<Event> events;
static std::vector<uint8_t> pendingResult;
static NSMutableDictionary *state;
static NSString *path;
static uint32_t app() { return (uint32_t)strtoul(getenv("SteamAppId")?:"0",nullptr,10); }
static void save() {
  NSData *bytes=[NSJSONSerialization dataWithJSONObject:state options:0 error:nil]; [bytes writeToFile:path atomically:YES];
}
static void event(int id,int result) {
  Event e{id,std::vector<uint8_t>(id==1101?20:12)}; uint64_t game=app(); memcpy(e.bytes.data(),&game,8);memcpy(e.bytes.data()+8,&result,4);
  if(id==1101) {
    uint64_t identity=strtoull([state[@"accountId"] UTF8String],nullptr,10);
    if([state[@"wrongRefreshIdentity"] boolValue]) ++identity;
    memcpy(e.bytes.data()+12,&identity,8);
    if([state[@"truncatedRefresh"] boolValue]) e.bytes.resize(12);
  }
  events.push_back(e);
}
static int createPipe(void*) { return 1; }
static bool releasePipe(void*,int) { return true; }
static int connectUser(void*,int) { return 1; }
static void releaseUser(void*,int,int) {}
static bool loggedOn(void*) { return true; }
static uint64_t identity(void*,uint64_t *out) {
  uint64_t result=strtoull([state[@"accountId"] UTF8String],nullptr,10);
  if(![state[@"registerIdentityOnly"] boolValue]) *out=result;
  return result;
}
static uint32_t getApp(void*) { return app(); }
static bool owns(void*,uint32_t id) { return id==42; }
static void *getUser(void*,int,int,const char *version) {return strcmp(version,"SteamUser012")==0?&user:nullptr;}
static void *getUtils(void*,int,const char *version) {return strcmp(version,"SteamUtils005")==0?&utils:nullptr;}
static void *getApps(void*,int,int,const char *version) {return strcmp(version,"STEAMAPPS_INTERFACE_VERSION008")==0?&apps:nullptr;}
static void *getStats(void*,int,int,const char *version) {return strcmp(version,"STEAMUSERSTATS_INTERFACE_VERSION013")==0?&stats:nullptr;}
static bool getFloat(void*,const char *id,float *value) { if(strcmp(id,"distance"))return false;*value=[state[@"distance"] floatValue];return true; }
static bool getInt(void*,const char *id,int32_t *value) { if(strcmp(id,"count"))return false;*value=[state[@"count"] intValue];return true; }
static bool setFloat(void*,const char *id,float value) {if(strcmp(id,"distance"))return false;state[@"distance"]=@(value);return true;}
static bool setInt(void*,const char *id,int32_t value) {if(strcmp(id,"count"))return false;state[@"count"]=@(value);return true;}
static bool setAchievement(void*,const char *id) {if(strcmp(id,"ACH"))return false;state[@"unlocked"]=@YES;return true;}
static bool clearAchievement(void*,const char *id) {if(strcmp(id,"ACH"))return false;state[@"unlocked"]=@NO;return true;}
static bool achievement(void*,const char *id,uint8_t *unlocked,uint32_t *when) {if(strcmp(id,"ACH"))return false;*unlocked=[state[@"unlocked"] boolValue];*when=*unlocked?1234:0;return true;}
static bool store(void*) {save();event(1102,1);return true;}
static const char *display(void*,const char*,const char *key) {return !strcmp(key,"name")?"Synthetic ABI achievement":!strcmp(key,"desc")?"Not a Steam account":!strcmp(key,"hidden")?"0":"";}
static uint32_t count(void*) {return 1;}
static const char *name(void*,uint32_t index) {return index==0?"ACH":nullptr;}
static uint64_t request(void*,uint64_t) {
  if([state[@"callResultRefresh"] boolValue]) {
    pendingResult.assign(20,0); uint64_t game=app(), identity=strtoull([state[@"accountId"] UTF8String],nullptr,10); int result=1;
    if([state[@"wrongRefreshIdentity"] boolValue]) ++identity;
    memcpy(pendingResult.data(),&game,8);memcpy(pendingResult.data()+8,&result,4);memcpy(pendingResult.data()+12,&identity,8);
    Event complete{703,std::vector<uint8_t>(16)};uint64_t handle=123;int callback=1101;uint32_t size=20;
    memcpy(complete.bytes.data(),&handle,8);memcpy(complete.bytes.data()+8,&callback,4);memcpy(complete.bytes.data()+12,&size,4);events.push_back(complete);
  } else {event(1101,8);event(1101,1);}
  return 123;
}
static bool getResult(void*,uint64_t handle,void *bytes,int size,int callback,uint8_t *failed) {
  if(handle!=123 || size!=20 || callback!=1101 || pendingResult.size()!=20)return false;
  *failed=0;memcpy(bytes,pendingResult.data(),20);return true;
}
static uint64_t requestGlobal(void*) {return 124;}
static bool percentage(void*,const char*,float *value) {*value=7.5f;return true;}
extern "C" __attribute__((visibility("default"))) bool Steam_BGetCallback(int,Message *message,int *call) {
  if(events.empty())return false;
  message->user=1;message->id=events.front().id;message->data=events.front().bytes.data();message->size=(int)events.front().bytes.size();*call=0;return true;
}
extern "C" __attribute__((visibility("default"))) void Steam_FreeLastCallback(int) {if(!events.empty())events.pop_front();}
extern "C" __attribute__((visibility("default"))) void *CreateInterface(const char *version,int*) {
  if(strcmp(version,"SteamClient018"))return nullptr;
  Dl_info info{};dladdr((void*)&CreateInterface,&info);
  NSString *library=[NSString stringWithUTF8String:info.dli_fname];
  NSString *root=library;
  for(int i=0;i<5;i++)root=root.stringByDeletingLastPathComponent;
  path=[root stringByAppendingPathComponent:@"synthetic-state.json"];
  state=[NSJSONSerialization JSONObjectWithData:[NSData dataWithContentsOfFile:path] options:NSJSONReadingMutableContainers error:nil];
  clientSlots[0]=(void*)createPipe;clientSlots[1]=(void*)releasePipe;clientSlots[2]=(void*)connectUser;clientSlots[4]=(void*)releaseUser;
  clientSlots[5]=(void*)getUser;clientSlots[9]=(void*)getUtils;clientSlots[13]=(void*)getStats;clientSlots[15]=(void*)getApps;
  userSlots[1]=(void*)loggedOn;userSlots[2]=(void*)identity;utilsSlots[9]=(void*)getApp;utilsSlots[13]=(void*)getResult;appsSlots[6]=(void*)owns;
  statsSlots[0]=(void*)getFloat;statsSlots[1]=(void*)getInt;statsSlots[2]=(void*)setFloat;statsSlots[3]=(void*)setInt;
  statsSlots[6]=(void*)setAchievement;statsSlots[7]=(void*)clearAchievement;statsSlots[8]=(void*)achievement;statsSlots[9]=(void*)store;
  statsSlots[11]=(void*)display;statsSlots[13]=(void*)count;statsSlots[14]=(void*)name;statsSlots[15]=(void*)request;
  statsSlots[33]=(void*)requestGlobal;statsSlots[36]=(void*)percentage;return &client;
}
