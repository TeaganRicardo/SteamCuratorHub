// Steam Curator Hub's independently implemented helper. No Steam SDK is linked.
// ABI/schema facts: zlib-licensed SAM/Enhanced, pinned in THIRD_PARTY_NOTICES.md.
// One AppID per process. Only an explicit read/apply request can connect Steam.
#import <Foundation/Foundation.h>
#include <CommonCrypto/CommonDigest.h>
#include <dlfcn.h>
#include <mach-o/loader.h>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <limits>
#include <memory>
#include <set>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>
#include <unistd.h>

using Clock = std::chrono::steady_clock;
static NSString *S(const std::string &v) { return [[NSString alloc] initWithBytes:v.data() length:v.size() encoding:NSUTF8StringEncoding] ?: @""; }
static std::string U(NSString *v) { return v ? std::string(v.UTF8String ?: "") : ""; }
static bool isDict(id v) { return [v isKindOfClass:[NSDictionary class]]; }
static bool isArray(id v) { return [v isKindOfClass:[NSArray class]]; }
static bool isString(id v) { return [v isKindOfClass:[NSString class]]; }
static bool isNumber(id v) { return [v isKindOfClass:[NSNumber class]] && CFGetTypeID((__bridge CFTypeRef)v) != CFBooleanGetTypeID(); }
static bool isBool(id v) { return [v isKindOfClass:[NSNumber class]] && CFGetTypeID((__bridge CFTypeRef)v) == CFBooleanGetTypeID(); }
static void require(bool ok, const char *message) { if (!ok) throw std::runtime_error(message); }
static uint32_t appIdOf(id value) {
  require(isNumber(value), "appId must be an integer");
  double n = [value doubleValue];
  require(std::isfinite(n) && n >= 1 && n <= UINT32_MAX && std::floor(n) == n, "Invalid appId");
  return (uint32_t)n;
}
static std::string safeId(id value) {
  require(isString(value) && [value length] > 0 && [value length] <= 512, "Invalid API item id");
  std::string s = U(value); require(s.find('\0') == std::string::npos, "Item id contains a null byte"); return s;
}
static bool sameValue(id a, id b) {
  if (isNumber(a) && isNumber(b)) return [a doubleValue] == [b doubleValue];
  return a && b && [a isEqual:b];
}

// Bounded binary KeyValues parser. Only the requested AppID's schema is opened.
class SchemaReader {
  const uint8_t *data; size_t size, pos = 0, nodes = 0;
  template<class T> T number() {
    require(pos + sizeof(T) <= size, "Truncated schema number"); T v; memcpy(&v, data + pos, sizeof(v)); pos += sizeof(v); return v;
  }
  NSString *string() {
    size_t start = pos;
    while (pos < size && data[pos]) ++pos;
    require(pos < size && pos - start <= 1048576, "Invalid schema string");
    NSString *s = [[NSString alloc] initWithBytes:data + start length:pos - start encoding:NSUTF8StringEncoding]; ++pos;
    require(s != nil, "Schema string is not UTF-8"); return s;
  }
  NSDictionary *object(size_t depth) {
    require(depth <= 64, "Schema depth exceeds 64"); NSMutableDictionary *result = [NSMutableDictionary dictionary];
    NSMutableSet *keys=[NSMutableSet set];
    for (;;) {
      uint8_t type = number<uint8_t>(); if (type == 8) return result;
      require(++nodes <= 200000, "Schema has too many entries"); NSString *key = string();
      NSString *folded=key.lowercaseString; require(![keys containsObject:folded], "Duplicate schema key"); [keys addObject:folded]; id value;
      switch (type) {
        case 0: value = object(depth + 1); break;
        case 1: value = string(); break;
        case 2: value = @(number<int32_t>()); break;
        case 3: { float n = number<float>(); require(std::isfinite(n), "Non-finite schema value"); value = @(n); break; }
        case 4: case 6: value = @(number<uint32_t>()); break;
        case 7: value = @(number<uint64_t>()); break;
        default: throw std::runtime_error("Unsupported binary schema type");
      }
      result[key] = value;
    }
  }
public:
  explicit SchemaReader(NSData *bytes): data((const uint8_t *)bytes.bytes), size(bytes.length) {
    require(size > 0 && size <= 8 * 1024 * 1024, "Invalid schema size");
  }
  NSDictionary *parse() { return object(0); }
};
static id field(NSDictionary *d, NSString *key) {
  if (!isDict(d)) return nil;
  id exact = d[key]; if (exact) return exact;
  for (NSString *k in d) if ([k caseInsensitiveCompare:key] == NSOrderedSame) return d[k];
  return nil;
}
static double bound(NSDictionary *d, NSString *key, double fallback) {
  id v = field(d, key);
  if (!(isNumber(v) || isString(v))) return fallback;
  if (isString(v)) { NSScanner *scanner = [NSScanner scannerWithString:v]; double n; return [scanner scanDouble:&n] && scanner.isAtEnd && std::isfinite(n) ? n : fallback; }
  double n = [v doubleValue]; return std::isfinite(n) ? n : fallback;
}
// Missing optional fields use documented defaults; malformed present fields
// never fall back to a permissive zero/default.
static double schemaNumber(NSDictionary *d, NSString *key, double fallback, bool &valid) {
  id value=field(d,key); if(!value) return fallback;
  double result=0; bool parsed=false;
  if(isNumber(value)) { result=[value doubleValue]; parsed=std::isfinite(result); }
  else if(isString(value)) {
    NSScanner *scanner=[NSScanner scannerWithString:value];
    parsed=[scanner scanDouble:&result] && scanner.isAtEnd && std::isfinite(result);
  }
  if(!parsed) {valid=false;return fallback;}
  return result;
}
static int schemaPermission(NSDictionary *d, bool &valid) {
  double permission=schemaNumber(d,@"permission",0,valid);
  if(permission<0 || permission>3 || std::floor(permission)!=permission) valid=false;
  return valid?(int)permission:-1;
}
static NSDictionary *schemaDefinitions(NSDictionary *tree, uint32_t appId) {
  NSDictionary *app = field(tree, [NSString stringWithFormat:@"%u", appId]); NSDictionary *stats = field(app, @"stats");
  if (!isDict(stats)) return @{@"known":@NO, @"achievements":@{}, @"stats":@[]};
  NSMutableDictionary *achievements = [NSMutableDictionary dictionary]; NSMutableArray *numeric = [NSMutableArray array];
  for (NSString *key in stats) {
    NSDictionary *item = stats[key]; if (!isDict(item)) continue;
    NSDictionary *bits = field(item, @"bits");
    if (isDict(bits)) for (NSString *bit in bits) {
      NSDictionary *a = bits[bit]; NSString *name = field(a, @"name");
      if (isString(name)) { bool valid=true; int permission=schemaPermission(a,valid); achievements[name]=valid ? @(permission) : [NSNull null]; }
    }
    auto resolveType=[](id raw)->int {
      if(!raw) return 0;
      if(isString(raw)) {
        NSString *name=[raw lowercaseString];
        if([name isEqual:@"int"] || [name isEqual:@"integer"]) return 1;
        if([name isEqual:@"float"]) return 2;
        if([name isEqual:@"averagerate"]) return 3;
        if([name isEqual:@"achievements"]) return 4;
        if([name isEqual:@"groupachievements"]) return 5;
      }
      bool valid=true; double value=schemaNumber(@{@"value":raw},@"value",0,valid);
      if(!valid || value<0 || value>5 || std::floor(value)!=value) return -1;
      return (int)value;
    };
    int type=resolveType(field(item,@"type"));
    if(type==0) type=resolveType(field(item,@"type_int"));
    if (type < 1 || type > 3) continue;
    NSString *name = field(item, @"name"); if (!isString(name) || !name.length) continue;
    bool valid=true; int permission=schemaPermission(item,valid);
    double increment=schemaNumber(item,@"incrementonly",0,valid);
    double minimum=schemaNumber(item,@"min",type==1?(double)INT32_MIN:-(double)std::numeric_limits<float>::max(),valid);
    double maximum=schemaNumber(item,@"max",type==1?(double)INT32_MAX:(double)std::numeric_limits<float>::max(),valid);
    double maximumChange=schemaNumber(item,@"maxchange",0,valid);
    if((increment!=0 && increment!=1) || minimum>maximum || maximumChange<0) valid=false;
    NSMutableDictionary *def = [@{@"id":name, @"type":type == 1 ? @"int" : type == 2 ? @"float" : @"average",
      @"writable":@(valid && (permission & 2) == 0 && type != 3), @"permission":@(permission),
      @"incrementOnly":@(increment!=0), @"min":@(minimum), @"max":@(maximum), @"maxChange":@(maximumChange)} mutableCopy];
    if(!valid) def[@"reason"]=@"Malformed or unsupported schema permission/constraint; write blocked";
    else if (permission & 2) def[@"reason"] = @"Schema marks this statistic as server/protected";
    else if (type == 3) def[@"reason"] = @"Average-rate statistics require a rate update, not direct assignment";
    [numeric addObject:def];
  }
  return @{@"known":@YES, @"achievements":achievements, @"stats":numeric};
}

struct CallbackMessage { int user; int id; const uint8_t *data; int size; };
static bool callbackMatches(const CallbackMessage &message, int wanted, int user, uint32_t appId, uint64_t expectedIdentity, int32_t &result) {
  if(message.user!=user || message.id!=wanted || !message.data || message.size<12) return false;
  uint64_t game=0; memcpy(&game,message.data,8); memcpy(&result,message.data+8,4);
  if((uint32_t)game!=appId) return false;
  if(wanted==1101) {
    // Valve __APPLE__ callbacks use pack(4): uint64 game, int32 result,
    // CSteamID bytes at offset 12 (20 bytes total). No Windows packing guess.
    if(message.size<20 || expectedIdentity==0) return false;
    uint64_t identity=0; memcpy(&identity,message.data+12,8);
    if(identity!=expectedIdentity) return false;
  }
  return true;
}

class Transport {
public:
  virtual ~Transport() = default;
  virtual NSDictionary *read() = 0;
  virtual NSString *account() = 0;
  virtual bool setAchievement(NSString *id, bool unlocked) = 0;
  virtual bool setStat(NSString *id, NSString *type, double value) = 0;
  virtual NSDictionary *store() = 0;
  virtual NSDictionary *fixtureState() { return nil; }
};

class FixtureTransport final: public Transport {
  NSMutableDictionary *state; NSDictionary *behavior; uint32_t appId;
public:
  FixtureTransport(NSDictionary *fixture, uint32_t id): appId(id) {
    NSData *bytes = [NSJSONSerialization dataWithJSONObject:fixture options:0 error:nil];
    state = [NSJSONSerialization JSONObjectWithData:bytes options:NSJSONReadingMutableContainers error:nil];
    behavior = isDict(state[@"behavior"]) ? state[@"behavior"] : @{};
    require(isString(state[@"accountId"]), "Fixture must contain accountId");
  }
  NSString *account() override { return state[@"accountId"]; }
  NSDictionary *read() override {
    if (isNumber(behavior[@"delayMs"])) {
      int delay=[behavior[@"delayMs"] intValue]; require(delay>=0 && delay<=2000,"Invalid synthetic delay");
      std::this_thread::sleep_for(std::chrono::milliseconds(delay));
    }
    if ([behavior[@"readFailure"] boolValue]) throw std::runtime_error("Synthetic read failure");
    NSMutableArray *stats = [state[@"stats"] mutableCopy] ?: [NSMutableArray array];
    NSArray *ach = state[@"achievements"] ?: @[];
    if (isString(state[@"schemaBase64"])) {
      NSData *data = [[NSData alloc] initWithBase64EncodedString:state[@"schemaBase64"] options:0]; require(data != nil, "Invalid fixture schema base64");
      NSDictionary *defs = schemaDefinitions(SchemaReader(data).parse(), appId); stats = [NSMutableArray array];
      NSMutableArray *schemaAchievements=[NSMutableArray array];
      for(NSDictionary *row in ach) {
        NSMutableDictionary *a=[row mutableCopy]; id permission=defs[@"achievements"][a[@"id"]];
        a[@"writable"]=@(isNumber(permission) && ([permission intValue]&3)==0);
        if(![a[@"writable"] boolValue]) a[@"reason"]=@"Schema achievement permission unavailable, malformed or protected";
        [schemaAchievements addObject:a];
      }
      ach=schemaAchievements;
      for (NSDictionary *def in defs[@"stats"]) {
        NSMutableDictionary *s = [def mutableCopy]; s[@"value"] = state[@"statValues"][s[@"id"]] ?: @0; [stats addObject:s];
      }
    }
    return @{@"appId":@(appId), @"accountId":account(), @"achievements":ach, @"stats":stats,
      @"source":@"fixture", @"capabilities":@{@"read":@YES, @"write":@YES, @"steamVerified":@NO,
      @"librarySha256":@"fixture", @"privateAbiLocallyVerified":@NO, @"refreshCallbackMatched":@YES}};
  }
  bool setAchievement(NSString *id, bool unlocked) override {
    if ([behavior[@"rejectIds"] containsObject:id]) return false;
    for (NSMutableDictionary *a in state[@"achievements"]) if ([a[@"id"] isEqual:id]) { a[@"unlocked"] = @(unlocked); return true; }
    return false;
  }
  bool setStat(NSString *id, NSString *type, double value) override {
    if ([behavior[@"rejectIds"] containsObject:id]) return false;
    for (NSMutableDictionary *s in state[@"stats"]) if ([s[@"id"] isEqual:id]) { s[@"value"] = [type isEqual:@"int"] ? @((int32_t)value) : @((float)value); return true; }
    if (state[@"statValues"][id]) { state[@"statValues"][id] = [type isEqual:@"int"] ? @((int32_t)value) : @((float)value); return true; }
    return false;
  }
  NSDictionary *store() override {
    if ([behavior[@"accountAfterStore"] isKindOfClass:[NSString class]]) state[@"accountId"] = behavior[@"accountAfterStore"];
    NSString *mode = behavior[@"store"] ?: @"ok";
    uint8_t bytes[12]{}; uint64_t game=[mode isEqual:@"wrongApp"] ? (uint64_t)appId+1 : appId;
    int32_t result=[mode isEqual:@"ok"] || [mode hasPrefix:@"wrong"] ? 1 : 8;
    memcpy(bytes,&game,8); memcpy(bytes+8,&result,4);
    CallbackMessage message{[mode isEqual:@"wrongUser"]?2:1,1102,bytes,[mode isEqual:@"shortPayload"]?8:12};
    int32_t received=0;
    if([mode isEqual:@"timeout"] || [mode isEqual:@"apiFalse"] || !callbackMatches(message,1102,1,appId,0,received))
      return @{@"status":@"unknown", @"reason":@"Matching store callback was not observed", @"synthetic":@YES};
    return @{@"status":received==1 ? @"ok" : @"failed", @"result":@(received), @"synthetic":@YES};
  }
  NSDictionary *fixtureState() override { return state; }
};

class SteamTransport final: public Transport {
  uint32_t appId; uint64_t expectedIdentity=0; int pipe = 0, user = 0, timeoutMs; void *module = nullptr, *client = nullptr, *userApi = nullptr, *utilsApi = nullptr, *statsApi = nullptr;
  NSString *libraryPath, *fingerprint, *steamRoot; NSDictionary *definitions;
  using GetCallback = uint8_t(*)(int, CallbackMessage *, int *);
  using FreeCallback = void(*)(int);
  GetCallback getCallback = nullptr; FreeCallback freeCallback = nullptr;
  template<class F> F slot(void *object, int index) {
    require(object != nullptr, "Null Steam interface"); void *p = (*(void ***)object)[index]; require(p != nullptr, "Missing Steam vtable entry"); return reinterpret_cast<F>(p);
  }
  void *interface(int index, const char *version) {
    void *p = slot<void *(*)(void *, int, int, const char *)>(client,index)(client,user,pipe,version);
    require(p != nullptr, "Required exact Steam interface version unavailable"); return p;
  }
  std::string cstring(const char *s) { return s ? std::string(s, strnlen(s, 65536)) : ""; }
  void drain() { CallbackMessage m{}; int call; for (int i=0; i<10000 && getCallback(pipe,&m,&call); ++i) freeCallback(pipe); }
  NSDictionary *wait(int wanted, int limitMs, bool waitForSuccess=false, uint64_t expectedCall=0) {
    NSDictionary *lastFailure=nil;
    auto start = Clock::now();
    while (std::chrono::duration_cast<std::chrono::milliseconds>(Clock::now()-start).count() < limitMs) {
      CallbackMessage m{}; int call; int count = 0;
      while (count++ < 10000 && getCallback(pipe,&m,&call)) {
        int32_t result = 0; bool match = callbackMatches(m,wanted,user,appId,expectedIdentity,result);
        // RequestUserStats is a call result. Some client versions deliver 703
        // (SteamAPICallCompleted_t) rather than a plain 1101 notification.
        if(!match && expectedCall && m.user==user && m.id==703 && m.data && m.size>=16) {
          uint64_t handle=0; int32_t callbackId=0; uint32_t payloadSize=0;
          memcpy(&handle,m.data,8); memcpy(&callbackId,m.data+8,4); memcpy(&payloadSize,m.data+12,4);
          if(handle==expectedCall && callbackId==wanted && payloadSize>=20 && payloadSize<=1048576) {
            std::vector<uint8_t> payload(payloadSize); uint8_t failed=0;
            bool fetched=slot<uint8_t(*)(void *,uint64_t,void *,int,int,uint8_t *)>(utilsApi,13)(utilsApi,handle,payload.data(),(int)payloadSize,wanted,&failed)!=0;
            CallbackMessage decoded{user,wanted,payload.data(),(int)payloadSize};
            if(fetched && !failed) match=callbackMatches(decoded,wanted,user,appId,expectedIdentity,result);
          }
        }
        freeCallback(pipe);
        if (match) {
          NSDictionary *received=@{@"status":result == 1 ? @"ok" : @"failed", @"result":@(result), @"appId":@(appId), @"callbackId":@(wanted)};
          if(result==1 || !waitForSuccess) return received;
          lastFailure=received; // Uncached schemas can emit a transient non-OK before the matching OK.
        }
      }
      std::this_thread::sleep_for(std::chrono::milliseconds(10));
    }
    if(lastFailure) return lastFailure;
    return @{@"status":@"unknown", @"reason":@"Timed out waiting for matching Steam callback", @"callbackId":@(wanted)};
  }
  void release() {
    if (client && pipe) {
      if (user) slot<void(*)(void *,int,int)>(client,4)(client,pipe,user);
      slot<uint8_t(*)(void *,int)>(client,1)(client,pipe);
    }
    pipe=0; user=0; // Do not dlclose: Steam may retain threads/TLS until process exit.
  }
public:
  static NSDictionary *metadata(NSString *path) {
#if !defined(__aarch64__) || !defined(__APPLE__)
    (void)path; throw std::runtime_error("Native ABI route is restricted to macOS arm64");
#else
    require(isString(path) && path.isAbsolutePath && [path.lastPathComponent isEqual:@"steamclient.dylib"], "Expected an absolute installed steamclient.dylib path");
    NSDictionary *attrs = [[NSFileManager defaultManager] attributesOfItemAtPath:path error:nil];
    require(attrs && [attrs[NSFileSize] unsignedLongLongValue] <= 512ULL*1024*1024, "Steam library missing or too large");
    NSData *bytes = [NSData dataWithContentsOfFile:path options:NSDataReadingMappedIfSafe error:nil]; require(bytes && bytes.length >= 32, "Cannot read Steam library metadata");
    const uint8_t *p=(const uint8_t *)bytes.bytes; bool arm=false;
    auto big32=[](const uint8_t *q)->uint32_t { return ((uint32_t)q[0]<<24)|((uint32_t)q[1]<<16)|((uint32_t)q[2]<<8)|q[3]; };
    uint32_t magic; memcpy(&magic,p,4);
    if (magic == MH_MAGIC_64) { uint32_t cpu; memcpy(&cpu,p+4,4); arm = cpu == 0x0100000c; }
    else if (big32(p) == 0xcafebabe || big32(p) == 0xcafebabf) {
      uint32_t n=big32(p+4); size_t width=big32(p)==0xcafebabf ? 32 : 20;
      require(n<=32 && 8+(size_t)n*width<=bytes.length, "Invalid fat Mach-O header");
      for(uint32_t i=0;i<n;++i) if(big32(p+8+i*width)==0x0100000c) arm=true;
    }
    require(arm, "Installed Steam library does not contain the guarded arm64 architecture");
    unsigned char digest[CC_SHA256_DIGEST_LENGTH]; CC_SHA256(bytes.bytes,(CC_LONG)bytes.length,digest); NSMutableString *hash=[NSMutableString string];
    for (unsigned char n: digest) [hash appendFormat:@"%02x",n];
    return @{@"libraryPath":path, @"librarySha256":hash, @"architecture":@"arm64", @"interfaceVersion":@"SteamClient018 / SteamUser012 / SteamUtils005 / ISteamUserStats013",
      @"abiSourceCommit":@"f6c7bbabad55d61802766d94b12bb4e16e8bbc76", @"privateAbiLocallyVerified":@NO};
#endif
  }
  SteamTransport(NSDictionary *request, uint32_t id): appId(id), timeoutMs(request[@"timeoutMs"] ? [request[@"timeoutMs"] intValue] : 12000) {
    require(timeoutMs>=10 && timeoutMs<=30000, "timeoutMs outside 10..30000");
    NSString *configured=request[@"steamLibraryPath"];
    if (!configured) configured=[NSHomeDirectory() stringByAppendingPathComponent:@"Library/Application Support/Steam"];
    libraryPath=[configured hasSuffix:@".dylib"] ? configured : [configured stringByAppendingPathComponent:@"Steam.AppBundle/Steam/Contents/MacOS/steamclient.dylib"];
    NSString *suffix=@"/Steam.AppBundle/Steam/Contents/MacOS/steamclient.dylib";
    require([libraryPath hasSuffix:suffix], "Library must be inside a user-installed Steam tree");
    steamRoot=[libraryPath substringToIndex:libraryPath.length-suffix.length];
    NSDictionary *meta=metadata(libraryPath); fingerprint=meta[@"librarySha256"];
    if (request[@"expectedLibrarySha256"]) require([request[@"expectedLibrarySha256"] isEqual:fingerprint], "Steam client changed since preview; reload before applying");
    require(setenv("SteamAppId",std::to_string(appId).c_str(),1)==0, "Cannot set process AppID");
    require(setenv("SteamGameId",std::to_string(appId).c_str(),1)==0, "Cannot set process GameID");
    module=dlopen(libraryPath.fileSystemRepresentation, RTLD_NOW|RTLD_LOCAL);
    require(module != nullptr, "Could not load installed Steam client library");
    try {
      auto create=reinterpret_cast<void *(*)(const char *,int *)>(dlsym(module,"CreateInterface"));
      getCallback=reinterpret_cast<GetCallback>(dlsym(module,"Steam_BGetCallback"));
      freeCallback=reinterpret_cast<FreeCallback>(dlsym(module,"Steam_FreeLastCallback"));
      require(create && getCallback && freeCallback, "Required Steam exports unavailable");
      client=create("SteamClient018",nullptr); require(client!=nullptr,"SteamClient018 unavailable");
      pipe=slot<int(*)(void *)>(client,0)(client); require(pipe!=0,"Steam pipe unavailable; start Steam first");
      user=slot<int(*)(void *,int)>(client,2)(client,pipe); require(user!=0,"Steam is not logged in");
      userApi=interface(5,"SteamUser012");
      require(slot<uint8_t(*)(void *)>(userApi,1)(userApi)!=0,"Steam client is offline");
      utilsApi=slot<void *(*)(void *,int,const char *)>(client,9)(client,pipe,"SteamUtils005");
      require(utilsApi && slot<uint32_t(*)(void *)>(utilsApi,9)(utilsApi)==appId,"Steam AppID context mismatch");
      void *apps=interface(15,"STEAMAPPS_INTERFACE_VERSION008");
      require(slot<uint8_t(*)(void *,uint32_t)>(apps,6)(apps,appId)!=0,"Active Steam account does not own this AppID");
      statsApi=interface(13,"STEAMUSERSTATS_INTERFACE_VERSION013");
      expectedIdentity=std::stoull(U(account())); drain();
      uint64_t call=slot<uint64_t(*)(void *,uint64_t)>(statsApi,15)(statsApi,std::stoull(U(account())));
      require(call!=0,"Steam stats refresh request failed");
      NSDictionary *received=wait(1101,timeoutMs,true,call); require([received[@"status"] isEqual:@"ok"],"Steam stats refresh callback missing or failed; no values were assumed");
      NSString *schema=[steamRoot stringByAppendingPathComponent:[NSString stringWithFormat:@"appcache/stats/UserGameStatsSchema_%u.bin",appId]];
      NSDictionary *attrs=[[NSFileManager defaultManager] attributesOfItemAtPath:schema error:nil];
      if (attrs && [attrs[NSFileSize] unsignedLongLongValue]<=8ULL*1024*1024) {
        NSData *bytes=[NSData dataWithContentsOfFile:schema];
        if(bytes) try { definitions=schemaDefinitions(SchemaReader(bytes).parse(),appId); } catch (...) { definitions=nil; }
      }
      if (!definitions) definitions=@{@"known":@NO,@"achievements":@{},@"stats":@[]};
    } catch (...) { release(); throw; }
  }
  ~SteamTransport() override { release(); }
  NSString *account() override {
    // Legacy SAM interfaces use an explicit output argument. Apple arm64
    // implementations may return the eight-byte identity in x0 instead. One
    // call supplies the output buffer and captures x0; an unused argument is
    // harmless for the register-return form. Never guess between identities.
    require(slot<uint8_t(*)(void *)>(userApi,1)(userApi)!=0,"Steam client went offline");
    uint64_t out=0;
    uint64_t returned=slot<uint64_t(*)(void *,uint64_t *)>(userApi,2)(userApi,&out);
    auto valid=[](uint64_t value){return value>=76561197960265728ULL && value<76561202255233024ULL;};
    require(valid(out) || valid(returned),"Invalid Steam identity; guarded ABI account read failed");
    require(!valid(out) || !valid(returned) || out==returned,"Ambiguous Steam identity from private interface");
    return S(std::to_string(valid(out)?out:returned));
  }
  NSDictionary *read() override {
    NSString *accountId=account(); NSMutableArray *ach=[NSMutableArray array], *stats=[NSMutableArray array];
    uint32_t n=slot<uint32_t(*)(void *)>(statsApi,13)(statsApi); require(n<=100000,"Unreasonable achievement count; ABI mismatch");
    (void)slot<uint64_t(*)(void *)>(statsApi,33)(statsApi); auto globalDeadline=Clock::now()+std::chrono::milliseconds(1500);
    for(uint32_t i=0;i<n;++i) {
      std::string id=cstring(slot<const char *(*)(void *,uint32_t)>(statsApi,14)(statsApi,i)); require(!id.empty(),"Empty achievement identifier");
      uint8_t unlocked=0; uint32_t when=0;
      require(slot<uint8_t(*)(void *,const char *,uint8_t *,uint32_t *)>(statsApi,8)(statsApi,id.c_str(),&unlocked,&when)!=0,"Achievement state read failed");
      auto display=[&](const char *key){return S(cstring(slot<const char *(*)(void *,const char *,const char *)>(statsApi,11)(statsApi,id.c_str(),key)));};
      NSNumber *permission=definitions[@"achievements"][S(id)]; bool writable=isNumber(permission) && ([permission intValue]&3)==0;
      NSMutableDictionary *a=[@{@"id":S(id), @"name":display("name"), @"description":display("desc"), @"hidden":@([display("hidden") isEqual:@"1"]),
        @"unlocked":@(unlocked!=0), @"unlockedAt":@(when), @"writable":@(writable)} mutableCopy];
      if(!writable) a[@"reason"]=!permission ? @"Achievement permissions unavailable" : !isNumber(permission) ? @"Malformed achievement permission; write blocked" : @"Schema marks this achievement as protected";
      [ach addObject:a];
    }
    bool first=true;
    for(NSMutableDictionary *a in ach) {
      float percentage=0; std::string id=U(a[@"id"]); bool ok=false;
      do {
        ok=slot<uint8_t(*)(void *,const char *,float *)>(statsApi,36)(statsApi,id.c_str(),&percentage)!=0;
        if(ok || !first || Clock::now()>=globalDeadline) break;
        drain(); std::this_thread::sleep_for(std::chrono::milliseconds(15));
      } while(true);
      first=false; if(ok && std::isfinite(percentage) && percentage>=0 && percentage<=100) a[@"globalPercent"]=@(percentage);
    }
    for(NSDictionary *def in definitions[@"stats"]) {
      std::string id=U(def[@"id"]); double value; bool ok;
      if([def[@"type"] isEqual:@"int"]) { int32_t v=0; ok=slot<uint8_t(*)(void *,const char *,int32_t *)>(statsApi,1)(statsApi,id.c_str(),&v); value=v; }
      else { float v=0; ok=slot<uint8_t(*)(void *,const char *,float *)>(statsApi,0)(statsApi,id.c_str(),&v); value=v; }
      NSMutableDictionary *s=[def mutableCopy];
      if(ok && std::isfinite(value)) s[@"value"]=@(value);
      else {s[@"value"]=[NSNull null]; s[@"writable"]=@NO; s[@"reason"]=@"Statistic value could not be read";}
      [stats addObject:s];
    }
    require([accountId isEqual:account()],"Steam account changed during read");
    NSMutableDictionary *caps=[metadata(libraryPath) mutableCopy];
    require([caps[@"librarySha256"] isEqual:fingerprint],"Steam client changed during read; reload before applying");
    caps[@"read"]=@YES; caps[@"write"]=definitions[@"known"]; caps[@"statsEnumeration"]=definitions[@"known"];
    caps[@"refreshCallbackMatched"]=@YES; caps[@"requiresSteamClient"]=@YES; caps[@"serverIndependentlyVerified"]=@NO;
    if(![definitions[@"known"] boolValue]) caps[@"warning"]=@"Requested game's schema unavailable: numeric enumeration and all writes are disabled; reload after Steam fetches schema";
    caps[@"presenceWarning"]=@"Reading this AppID through local Steam may temporarily change your playing presence";
    return @{@"appId":@(appId), @"accountId":accountId, @"achievements":ach, @"stats":stats, @"source":@"steam-client-refreshed", @"capabilities":caps};
  }
  bool setAchievement(NSString *id, bool unlocked) override {
    return slot<uint8_t(*)(void *,const char *)>(statsApi,unlocked?6:7)(statsApi,id.UTF8String)!=0;
  }
  bool setStat(NSString *id, NSString *type, double value) override {
    if([type isEqual:@"int"]) return slot<uint8_t(*)(void *,const char *,int32_t)>(statsApi,3)(statsApi,id.UTF8String,(int32_t)value)!=0;
    return slot<uint8_t(*)(void *,const char *,float)>(statsApi,2)(statsApi,id.UTF8String,(float)value)!=0;
  }
  NSDictionary *store() override {
    drain();
    if(!slot<uint8_t(*)(void *)>(statsApi,9)(statsApi)) return @{@"status":@"unknown",@"reason":@"StoreStats returned false after local staging; Steam may still persist pending values"};
    return wait(1102,timeoutMs);
  }
};

static NSDictionary *apply(Transport &transport, NSDictionary *request, NSDictionary *before, bool &mutationAttempted) {
  require(isString(request[@"expectedAccountId"]) && [request[@"expectedAccountId"] isEqual:transport.account()],"Steam account mismatch; no writes attempted");
  NSDictionary *changes=request[@"changes"]; require(isDict(changes),"changes must be an object");
  NSMutableArray *items=[NSMutableArray array]; std::set<std::string> seen;
  for(NSString *kind in @[@"achievements",@"stats"]) {
    NSArray *list=changes[kind] ?: @[]; require(isArray(list) && list.count<=10000,"Invalid changes array");
    for(id raw in list) {
      require(isDict(raw),"Each change must be an object"); std::string itemId=safeId(raw[@"id"]);
      require(seen.insert(U(kind)+":"+itemId).second,"Duplicate item in change batch");
      NSDictionary *original=nil;
      for(NSDictionary *row in before[kind]) if([row[@"id"] isEqual:S(itemId)]) {original=row;break;}
      bool achievement=[kind isEqual:@"achievements"]; id desired=raw[achievement?@"unlocked":@"value"];
      NSMutableDictionary *item=[@{@"kind":achievement?@"achievement":@"stat",@"id":S(itemId),@"requested":desired?:[NSNull null],
        @"staging":@"rejected",@"status":@"failed"} mutableCopy];
      NSString *reason=nil; id current=original[achievement?@"unlocked":@"value"];
      if(!original) reason=@"Item not found in fresh read";
      else if(![original[@"writable"] boolValue]) reason=original[@"reason"]?:@"Item is not writable";
      else if((achievement && !isBool(raw[@"before"])) || (!achievement && !isNumber(raw[@"before"])) || !sameValue(current,raw[@"before"])) reason=@"Preview is stale or before value is missing; reload before applying";
      else if(achievement && !isBool(desired)) reason=@"Achievement value must be boolean";
      else if(!achievement) {
        NSString *type=raw[@"type"];
        if(!isString(type) || ![type isEqual:original[@"type"]] || (! [type isEqual:@"int"] && ![type isEqual:@"float"])) reason=@"Statistic type mismatch";
        else if(!isNumber(desired) || !std::isfinite([desired doubleValue]) || !isNumber(current)) reason=@"Statistic requires a finite numeric value";
        else {
          double v=[desired doubleValue], prior=[current doubleValue]; bool integer=[type isEqual:@"int"];
          if(integer && (std::floor(v)!=v || v<INT32_MIN || v>INT32_MAX)) reason=@"Value is outside int32 range or is fractional";
          else if(!integer && std::abs(v)>std::numeric_limits<float>::max()) reason=@"Value is outside finite float32 range";
          else if(v<bound(original,@"min",integer?(double)INT32_MIN:-(double)std::numeric_limits<float>::max()) || v>bound(original,@"max",integer?(double)INT32_MAX:(double)std::numeric_limits<float>::max())) reason=@"Value violates schema min/max";
          else if([original[@"incrementOnly"] boolValue] && v<prior) reason=@"Statistic can only increase";
          else if(bound(original,@"maxChange",0)>0 && std::abs(v-prior)>bound(original,@"maxChange",0)) reason=@"Value violates schema maxChange";
          item[@"type"]=type;
        }
      }
      if(current) item[@"before"]=current;
      if(reason) item[@"reason"]=reason;
      else {item[@"staging"]=@"ready";item[@"status"]=@"pendingVerification";}
      [items addObject:item];
    }
  }
  require(items.count>0,"No changes selected");
  require([request[@"expectedAccountId"] isEqual:transport.account()],"Steam account changed before staging; no writes attempted");
  NSDictionary *stored=@{@"status":@"notAttempted"}; bool staged=false;
  try {
    for(NSMutableDictionary *item in items) if([item[@"staging"] isEqual:@"ready"]) {
      if(![request[@"expectedAccountId"] isEqual:transport.account()]) throw std::runtime_error("Steam account changed during staging");
      mutationAttempted=true;
      bool ok=[item[@"kind"] isEqual:@"achievement"] ? transport.setAchievement(item[@"id"],[item[@"requested"] boolValue]) : transport.setStat(item[@"id"],item[@"type"],[item[@"requested"] doubleValue]);
      item[@"staging"]=ok?@"accepted":@"rejected";
      item[@"status"]=ok?@"pendingVerification":@"failed";
      if(ok) staged=true; else item[@"reason"]=@"Steam rejected Set/Clear call";
    }
    if(staged) stored=transport.store();
  } catch(const std::exception &e) { stored=@{@"status":@"unknown",@"reason":S(e.what())}; }
  for(NSMutableDictionary *item in items) if([item[@"staging"] isEqual:@"ready"]) {item[@"status"]=@"failed";item[@"staging"]=@"notAttempted";item[@"reason"]=@"Batch interrupted before this item";}
  NSMutableDictionary *result=[@{@"appId":request[@"appId"],@"accountId":before[@"accountId"],@"source":before[@"source"],@"items":items,@"store":stored,@"mutationAttempted":@(mutationAttempted)} mutableCopy];
  if(transport.fixtureState()) result[@"fixtureState"]=transport.fixtureState();
  return result;
}

int main(int argc, const char *argv[]) {
  @autoreleasepool {
    bool mutation = false;
    FILE *protocolOutput=fdopen(dup(STDOUT_FILENO),"w");
    if(!protocolOutput) return 2;
    // Third-party client diagnostics cannot corrupt the JSON protocol.
    dup2(STDERR_FILENO,STDOUT_FILENO);
    @try {
    try {
      if(argc==2 && std::string(argv[1])=="--version") {
        fprintf(protocolOutput,"steam-curator-native-stats/1 SteamClient018 ISteamUserStats013 macOS-arm64\n"); return 0;
      }
      NSMutableData *input=[NSMutableData data]; uint8_t chunk[8192]; size_t length;
      while((length=fread(chunk,1,sizeof(chunk),stdin))>0) {require(input.length+length<=8*1024*1024,"Request exceeds 8 MiB");[input appendBytes:chunk length:length];}
      NSError *error=nil; id parsed=[NSJSONSerialization JSONObjectWithData:input options:0 error:&error]; require(isDict(parsed),"Expected JSON request object");
      NSDictionary *request=parsed; uint32_t appId=appIdOf(request[@"appId"]); NSString *operation=request[@"operation"];
      require([@[@"read",@"apply",@"metadata"] containsObject:operation],"Unknown helper operation");
      id fixture=request[@"fixture"]; require(!fixture || isDict(fixture),"Fixture must be an object");
      if(!fixture && [operation isEqual:@"apply"])
        require(isString(request[@"expectedLibrarySha256"]) && [request[@"expectedLibrarySha256"] length]==64,"Applying requires the client SHA-256 from a read preview");
      std::unique_ptr<Transport> transport;
      if(fixture) transport=std::make_unique<FixtureTransport>(fixture,appId);
      else if([operation isEqual:@"metadata"]) {
        NSString *path=request[@"steamLibraryPath"] ?: [NSHomeDirectory() stringByAppendingPathComponent:@"Library/Application Support/Steam/Steam.AppBundle/Steam/Contents/MacOS/steamclient.dylib"];
        NSData *json=[NSJSONSerialization dataWithJSONObject:@{@"ok":@YES,@"data":SteamTransport::metadata(path)} options:0 error:nil]; fwrite(json.bytes,1,json.length,protocolOutput); fputc('\n',protocolOutput); fflush(protocolOutput); return 0;
      } else transport=std::make_unique<SteamTransport>(request,appId);
      NSDictionary *snapshot=transport->read(), *result=snapshot;
      if([operation isEqual:@"apply"]) result=apply(*transport,request,snapshot,mutation);
      NSData *json=[NSJSONSerialization dataWithJSONObject:@{@"ok":@YES,@"data":result} options:0 error:&error]; require(json!=nil,"Result is not serializable");
      fwrite(json.bytes,1,json.length,protocolOutput); fputc('\n',protocolOutput); fflush(protocolOutput); return 0;
    } catch(const std::exception &e) {
      NSDictionary *result=@{@"ok":@NO,@"error":S(e.what()),@"mutationOutcome":mutation?@"unknown":@"notAttempted"};
      NSData *json=[NSJSONSerialization dataWithJSONObject:result options:0 error:nil]; fwrite(json.bytes,1,json.length,protocolOutput); fputc('\n',protocolOutput); fflush(protocolOutput); return 1;
    }
    } @catch(NSException *e) {
      NSData *json=[NSJSONSerialization dataWithJSONObject:@{@"ok":@NO,@"error":e.reason?:@"Native exception",@"mutationOutcome":mutation?@"unknown":@"notAttempted"} options:0 error:nil];
      fwrite(json.bytes,1,json.length,protocolOutput); fputc('\n',protocolOutput); fflush(protocolOutput); return 1;
    }
  }
}
