#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <dbghelp.h>
#include <tlhelp32.h>
#include <bcrypt.h>
#include <string>
#include <vector>
#include <sstream>
#include <iomanip>
#include <algorithm>
#include <cstdint>
#pragma comment(lib,"dbghelp.lib")
#pragma comment(lib,"bcrypt.lib")
static_assert(sizeof(void*)==4,"The capture helper MUST be built for x86");
namespace {
constexpr ULONGLONG MaxDump=16ull*1024*1024;
struct Handle { HANDLE h=INVALID_HANDLE_VALUE; Handle()=default; explicit Handle(HANDLE v):h(v){} ~Handle(){if(h&&h!=INVALID_HANDLE_VALUE)CloseHandle(h);} operator HANDLE()const{return h;} bool ok()const{return h&&h!=INVALID_HANDLE_VALUE;} };
ULONGLONG stamp(FILETIME t){return (ULONGLONG(t.dwHighDateTime)<<32)|t.dwLowDateTime;}
ULONGLONG created(HANDLE h){FILETIME c,e,k,u;return GetProcessTimes(h,&c,&e,&k,&u)?stamp(c):0;}
std::string utc(){SYSTEMTIME t;GetSystemTime(&t);char b[64];sprintf_s(b,"%04u-%02u-%02uT%02u:%02u:%02u.%03uZ",t.wYear,t.wMonth,t.wDay,t.wHour,t.wMinute,t.wSecond,t.wMilliseconds);return b;}
std::string hex(ULONGLONG n){std::ostringstream s;s<<"0x"<<std::hex<<std::uppercase<<n;return s.str();}
std::wstring pathOf(HANDLE h){std::wstring s(32768,0);DWORD n=DWORD(s.size());if(!QueryFullProcessImageNameW(h,0,s.data(),&n))return {};s.resize(n);return s;}
std::wstring parent(std::wstring p){auto x=p.find_last_of(L"\\/");return x==p.npos?L"":p.substr(0,x);}
bool plain(std::wstring p){if(p.size()<3||p[1]!=L':'||p[2]!=L'\\')return false;while(p.size()>3){DWORD a=GetFileAttributesW(p.c_str());if(a!=INVALID_FILE_ATTRIBUTES&&(a&FILE_ATTRIBUTE_REPARSE_POINT))return false;p=parent(p);}return true;}
bool readAll(HANDLE h,void* p,DWORD n){auto b=(BYTE*)p;while(n){DWORD r=0;if(!ReadFile(h,b,n,&r,nullptr)||!r)return false;b+=r;n-=r;}return true;}
std::wstring readString(HANDLE h){DWORD n=0;if(!readAll(h,&n,4)||!n||n>32760)throw DWORD(ERROR_INVALID_DATA);std::wstring s(n,0);if(!readAll(h,s.data(),n*2))throw DWORD(ERROR_BROKEN_PIPE);return s;}
void reply(HANDLE h,DWORD code){DWORD w=0;WriteFile(h,&code,4,&w,nullptr);}
std::string safeName(const std::wstring& p){auto pos=p.find_last_of(L"\\/");auto n=p.substr(pos==p.npos?0:pos+1);if(n.empty()||n.size()>128)return "UnknownModule";std::string r;for(auto c:n){if(!((c>=L'a'&&c<=L'z')||(c>=L'A'&&c<=L'Z')||(c>=L'0'&&c<=L'9')||c==L'.'||c==L'_'||c==L'-'))return "UnknownModule";r+=char(c);}return r;}
bool save(const std::wstring& file,const std::string& text){if(!plain(file))return false;Handle h(CreateFileW(file.c_str(),GENERIC_WRITE,FILE_SHARE_READ,nullptr,CREATE_ALWAYS,FILE_ATTRIBUTE_NORMAL,nullptr));DWORD n=0;return h.ok()&&WriteFile(h,text.data(),DWORD(text.size()),&n,nullptr)&&n==text.size();}
std::wstring dir;DWORD pid=0;ULONGLONG creation=0;
void state(const char* value,const char* stage,DWORD error=0){std::ostringstream s;s<<"{\"Pid\":"<<pid<<",\"CreatedFileTime\":"<<creation<<",\"Utc\":\""<<utc()<<"\",\"State\":\""<<value<<"\",\"Stage\":\""<<stage<<"\",\"Error\":"<<error<<"}";if(save(dir+L"\\state.tmp",s.str())&&plain(dir+L"\\state.json"))MoveFileExW((dir+L"\\state.tmp").c_str(),(dir+L"\\state.json").c_str(),MOVEFILE_REPLACE_EXISTING|MOVEFILE_WRITE_THROUGH);}
std::string hashFile(const std::wstring& p){
 if(!plain(p))return "";Handle f(CreateFileW(p.c_str(),GENERIC_READ,FILE_SHARE_READ|FILE_SHARE_WRITE|FILE_SHARE_DELETE,nullptr,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,nullptr));LARGE_INTEGER n;
 if(!f.ok()||!GetFileSizeEx(f,&n)||n.QuadPart>256ll*1024*1024)return "";
 BCRYPT_ALG_HANDLE a=nullptr;BCRYPT_HASH_HANDLE h=nullptr;BYTE digest[32];std::string result;
 if(BCryptOpenAlgorithmProvider(&a,BCRYPT_SHA256_ALGORITHM,nullptr,0)<0)return "";
 if(BCryptCreateHash(a,&h,nullptr,0,nullptr,0,0)>=0){BYTE b[65536];DWORD got=0;bool good=true;while(ReadFile(f,b,sizeof(b),&got,nullptr)&&got){if(BCryptHashData(h,b,got,0)<0){good=false;break;}}
 if(good&&BCryptFinishHash(h,digest,32,0)>=0){char b[3];for(BYTE v:digest){sprintf_s(b,"%02x",v);result+=b;}}BCryptDestroyHash(h);}BCryptCloseAlgorithmProvider(a,0);return result;
}
struct Module {DWORD base,size;std::string name,hash;};
std::vector<Module> modules(DWORD id){std::vector<Module> m;Handle snap(CreateToolhelp32Snapshot(TH32CS_SNAPMODULE,id));MODULEENTRY32W e{};e.dwSize=sizeof(e);if(snap.ok()&&Module32FirstW(snap,&e)){do {m.push_back({DWORD(e.modBaseAddr),e.modBaseSize,safeName(e.szModule),hashFile(e.szExePath)});}while(m.size()<256&&Module32NextW(snap,&e));}return m;}
std::string location(ULONGLONG addr,const std::vector<Module>& ms){for(auto& m:ms)if(addr>=m.base&&addr<ULONGLONG(m.base)+m.size)return "{\"Module\":\""+m.name+"\",\"Rva\":\""+hex(addr-m.base)+"\"}";return "{\"Module\":\"UnknownModule\",\"Rva\":null}";}
struct Budget {HANDLE file;ULONGLONG started,bytes=0;bool limited=false;};
// MS_VC_EXCEPTION is a debugger protocol, not necessarily a program fault.
// Consume only its validated x86 THREADNAME_INFO shape. Never log the name.
bool threadNameNotification(HANDLE process,const DEBUG_EVENT& event){
 const auto& ex=event.u.Exception.ExceptionRecord;
 if(event.dwDebugEventCode!=EXCEPTION_DEBUG_EVENT||event.dwProcessId!=GetProcessId(process)||
    ex.ExceptionCode!=0x406D1388||ex.ExceptionFlags!=0||ex.NumberParameters!=4||
    ex.ExceptionInformation[0]!=0x1000||ex.ExceptionInformation[3]!=0)return false;
 DWORD tid=DWORD(ex.ExceptionInformation[2]);if(tid==DWORD(-1))tid=event.dwThreadId;
 Handle target(OpenThread(THREAD_QUERY_LIMITED_INFORMATION|SYNCHRONIZE,FALSE,tid));
 if(!target.ok()||GetProcessIdOfThread(target)!=event.dwProcessId||WaitForSingleObject(target,0)!=WAIT_TIMEOUT)return false;
 const ULONG_PTR name=ex.ExceptionInformation[1];if(!name||name>UINT32_MAX-255)return false;
 // Byte-wise reads avoid requiring accessibility past a terminator/page edge.
 // Reject guards and inaccessible pages without changing their protection.
 for(unsigned i=0;i<256;i++){
  auto address=reinterpret_cast<LPCVOID>(name+i);MEMORY_BASIC_INFORMATION region{};
  if(!VirtualQueryEx(process,address,&region,sizeof(region))||region.State!=MEM_COMMIT||
     (region.Protect&(PAGE_GUARD|PAGE_NOACCESS)))return false;
  unsigned char c=0;SIZE_T got=0;
  if(!ReadProcessMemory(process,address,&c,1,&got)||got!=1)return false;
  if(!c)return i!=0;
  if(c<0x20||c==0x7f)return false;
 }
 return false;
}
bool memoryAccessException(const EXCEPTION_RECORD& ex){
 return (ex.ExceptionCode==EXCEPTION_ACCESS_VIOLATION||ex.ExceptionCode==EXCEPTION_IN_PAGE_ERROR)&&ex.NumberParameters>=2;
}
BOOL CALLBACK dumpCallback(PVOID opaque,const PMINIDUMP_CALLBACK_INPUT in,PMINIDUMP_CALLBACK_OUTPUT out){
 auto& b=*(Budget*)opaque;bool timeout=GetTickCount64()-b.started>5000;
 switch(in->CallbackType){
 case IoStartCallback:out->Status=S_FALSE;return TRUE;
 case IoWriteAllCallback:{
  ULONGLONG end=in->Io.Offset+in->Io.BufferBytes;
  if(timeout||end<in->Io.Offset||end>MaxDump){b.limited=true;out->Status=HRESULT_FROM_WIN32(ERROR_FILE_TOO_LARGE);return FALSE;}
  LARGE_INTEGER off;off.QuadPart=in->Io.Offset;DWORD done=0;
  bool ok=SetFilePointerEx(b.file,off,nullptr,FILE_BEGIN)&&WriteFile(b.file,in->Io.Buffer,in->Io.BufferBytes,&done,nullptr)&&done==in->Io.BufferBytes;
  out->Status=ok?S_OK:HRESULT_FROM_WIN32(GetLastError());b.bytes=std::max(b.bytes,end);return ok;
 }
 case IoFinishCallback:out->Status=S_OK;return TRUE;
 case CancelCallback:out->CheckCancel=TRUE;out->Cancel=timeout;return TRUE;
 case MemoryCallback:out->MemoryBase=0;out->MemorySize=0;return FALSE;
 default:return TRUE;
 }
}
bool capture(HANDLE process,const DEBUG_EVENT& event){
 auto& ex=event.u.Exception.ExceptionRecord;
 Handle claim(CreateFileW((parent(dir)+L"\\fatal.claim").c_str(),GENERIC_WRITE,FILE_SHARE_READ,nullptr,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,nullptr));
 if(!claim.ok()){state("LimitReached","Limit",GetLastError());return false;}
 state("Preparing","Context");Handle thread(OpenThread(THREAD_GET_CONTEXT|THREAD_QUERY_INFORMATION,FALSE,event.dwThreadId));
 CONTEXT ctx{};ctx.ContextFlags=CONTEXT_FULL|CONTEXT_FLOATING_POINT|CONTEXT_EXTENDED_REGISTERS;
 if(!thread.ok()||!GetThreadContext(thread,&ctx)){state("Failed","Context",GetLastError());return false;}
 EXCEPTION_RECORD record=ex;record.ExceptionRecord=nullptr;EXCEPTION_POINTERS pointers{&record,&ctx};
 MINIDUMP_EXCEPTION_INFORMATION info{event.dwThreadId,&pointers,FALSE}; // pointers are in THIS helper, not in the debuggee
 std::wstring file=dir+L"\\exception.dmp";Handle dump(CreateFileW(file.c_str(),GENERIC_WRITE,FILE_SHARE_READ,nullptr,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,nullptr));
 if(!dump.ok()){state("Failed","Dump",GetLastError());return false;}
 Budget budget{dump.h,GetTickCount64()};MINIDUMP_CALLBACK_INFORMATION cb{dumpCallback,&budget};
 state("Preparing","Dump");
 BOOL ok=MiniDumpWriteDump(process,pid,dump,MINIDUMP_TYPE(MiniDumpNormal|MiniDumpFilterMemory|MiniDumpFilterModulePaths|MiniDumpWithoutOptionalData),&info,nullptr,&cb);
 DWORD err=ok?0:GetLastError();CloseHandle(dump.h);dump.h=INVALID_HANDLE_VALUE;
 if(!ok){DeleteFileW(file.c_str());state("Failed","Dump",budget.limited?ERROR_FILE_TOO_LARGE:err);return false;}
 // Every DbgHelp call is on this one debugger thread. No network symbol path is used.
 auto ms=modules(pid);std::ostringstream s;
 s<<"{\"Schema\":1,\"Pid\":"<<pid<<",\"CreatedFileTime\":"<<creation<<",\"Utc\":\""<<utc()<<"\",\"ThreadId\":"<<event.dwThreadId<<",\"FirstChance\":false,\"ExceptionCode\":\""<<hex(ex.ExceptionCode)<<"\",\"ExceptionAddress\":\""<<hex(DWORD(ex.ExceptionAddress))<<"\",\"Fault\":"<<location(DWORD(ex.ExceptionAddress),ms)<<",\"AccessType\":";
 if(memoryAccessException(ex))s<<ex.ExceptionInformation[0];else s<<"null";
 s<<",\"AccessAddress\":";if(memoryAccessException(ex))s<<"\""<<hex(ex.ExceptionInformation[1])<<"\"";else s<<"null";
 s<<",\"Registers\":{\"Eip\":\""<<hex(ctx.Eip)<<"\",\"Esp\":\""<<hex(ctx.Esp)<<"\",\"Ebp\":\""<<hex(ctx.Ebp)<<"\",\"Eax\":\""<<hex(ctx.Eax)<<"\",\"Ebx\":\""<<hex(ctx.Ebx)<<"\",\"Ecx\":\""<<hex(ctx.Ecx)<<"\",\"Edx\":\""<<hex(ctx.Edx)<<"\",\"Esi\":\""<<hex(ctx.Esi)<<"\",\"Edi\":\""<<hex(ctx.Edi)<<"\",\"EFlags\":\""<<hex(ctx.EFlags)<<"\"},\"Frames\":[";
 s<<location(ctx.Eip,ms);CONTEXT walk=ctx;STACKFRAME64 frame{};frame.AddrPC={ctx.Eip,0,AddrModeFlat};frame.AddrStack={ctx.Esp,0,AddrModeFlat};frame.AddrFrame={ctx.Ebp,0,AddrModeFlat};
 SymSetOptions(SYMOPT_DEFERRED_LOADS|SYMOPT_FAIL_CRITICAL_ERRORS|SYMOPT_NO_PROMPTS|SYMOPT_IGNORE_NT_SYMPATH);
 bool symbols=SymInitialize(process,"",TRUE)!=FALSE;unsigned count=1;
 if(symbols){for(unsigned i=0;i<63;i++){if(!StackWalk64(IMAGE_FILE_MACHINE_I386,process,thread,&frame,&walk,nullptr,SymFunctionTableAccess64,SymGetModuleBase64,nullptr)||!frame.AddrPC.Offset)break;s<<","<<location(frame.AddrPC.Offset,ms);++count;}SymCleanup(process);}
 s<<"],\"StackMethod\":\""<<(symbols?"StackWalk64":"ContextOnly")<<"\",\"FrameCount\":"<<count<<",\"Modules\":[";
 bool comma=false;for(auto& m:ms){if(comma)s<<",";comma=true;s<<"{\"Name\":\""<<m.name<<"\",\"Base\":\""<<hex(m.base)<<"\",\"Size\":"<<m.size<<",\"Sha256\":\""<<m.hash<<"\"}";}
 s<<"],\"DumpBytes\":"<<budget.bytes<<",\"Privacy\":\"Private opt-in exception capture; not guaranteed free of sensitive stack data\"}";
 if(!save(dir+L"\\exception.json",s.str())){state("Failed","Captured",GetLastError());return false;}
 state("Captured","Captured");return true;
}
}
int wmain(int argc,wchar_t** argv){
 if(argc!=4)return 2;std::wstring pipeName=argv[1];if(pipeName.rfind(L"SCBL.LAN.Capture-",0)!=0||pipeName.size()!=49)return 2;
 DWORD parentPid=wcstoul(argv[2],nullptr,10);ULONGLONG parentCreated=_wcstoui64(argv[3],nullptr,10);
 Handle owner(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION|SYNCHRONIZE,FALSE,parentPid));if(!owner.ok()||created(owner)!=parentCreated)return 3;
 std::wstring pipePath=L"\\\\.\\pipe\\"+pipeName;
 if(!WaitNamedPipeW(pipePath.c_str(),5000))return 4;
 Handle pipe(CreateFileW(pipePath.c_str(),GENERIC_READ|GENERIC_WRITE,0,nullptr,OPEN_EXISTING,0,nullptr));DWORD server=0;
 if(!pipe.ok()||!GetNamedPipeServerProcessId(pipe,&server)||server!=parentPid)return 5;
 bool attached=false,ready=false,completed=false;DWORD lifetime=600;Handle process;
 try {
  DWORD magic=0;if(!readAll(pipe,&magic,4)||magic!=0x53434C43||!readAll(pipe,&pid,4)||!readAll(pipe,&creation,8))throw DWORD(ERROR_INVALID_DATA);
  std::wstring expected=readString(pipe);dir=readString(pipe);if(!readAll(pipe,&lifetime,4))throw DWORD(ERROR_INVALID_DATA);
  lifetime=std::clamp<DWORD>(lifetime,1,600);
  if(!plain(dir)||GetFileAttributesW(dir.c_str())==INVALID_FILE_ATTRIBUTES||safeName(expected)!="Blacklist_DX11_game.exe"||safeName(parent(dir))!="capture"||safeName(dir)!=std::to_string(pid)+"-"+std::to_string(creation))throw DWORD(ERROR_ACCESS_DENIED);
  process.h=OpenProcess(PROCESS_QUERY_INFORMATION|PROCESS_VM_READ|SYNCHRONIZE,FALSE,pid);
  if(!process.ok()||created(process)!=creation||_wcsicmp(pathOf(process).c_str(),expected.c_str())!=0||WaitForSingleObject(process,0)!=WAIT_TIMEOUT){state("Failed","Identity",ERROR_INVALID_PARAMETER);reply(pipe,ERROR_INVALID_PARAMETER);return 6;}
  USHORT pm=0,nm=0;if(!IsWow64Process2(process,&pm,&nm)||!((pm==IMAGE_FILE_MACHINE_I386)||(pm==0&&nm==IMAGE_FILE_MACHINE_I386))){state("Failed","Identity",ERROR_BAD_EXE_FORMAT);reply(pipe,ERROR_BAD_EXE_FORMAT);return 7;}
  if(!DebugActiveProcess(pid)){DWORD err=GetLastError();state("Failed","Attach",err);reply(pipe,err);return 8;}attached=true;
  // Nothing between attach and disabling kill-on-debugger-exit. Never enable SeDebugPrivilege.
  if(!DebugSetProcessKillOnExit(FALSE)){DWORD err=GetLastError();DebugActiveProcessStop(pid);attached=false;state("Failed","KillOnExit",err);reply(pipe,err);return 9;}
  // ACK means the debug port is installed and kill-on-exit is disabled. The launcher
  // may now resume its suspended bootstrap. Attach breakpoint still needs normal handling.
  state("Armed","Ready");reply(pipe,0);
  ULONGLONG started=GetTickCount64();
  while(true){
   if(GetTickCount64()-started>ULONGLONG(lifetime)*1000){if(!completed)state("Expired","Timeout");break;}
   DWORD available=0;if(WaitForSingleObject(owner,0)!=WAIT_TIMEOUT||!PeekNamedPipe(pipe,nullptr,0,nullptr,&available,nullptr)){if(!completed)state("Detached","ParentClosed");break;}
   DEBUG_EVENT e{};if(!WaitForDebugEvent(&e,100)){DWORD error=GetLastError();if(error==ERROR_SEM_TIMEOUT)continue;if(!completed)state("Failed","Wait",error);break;}
   DWORD continuation=DBG_CONTINUE;bool exiting=e.dwDebugEventCode==EXIT_PROCESS_DEBUG_EVENT;
   if(e.dwDebugEventCode==CREATE_PROCESS_DEBUG_EVENT){if(e.u.CreateProcessInfo.hFile)CloseHandle(e.u.CreateProcessInfo.hFile);}
   else if(e.dwDebugEventCode==LOAD_DLL_DEBUG_EVENT){if(e.u.LoadDll.hFile)CloseHandle(e.u.LoadDll.hFile);}
   else if(e.dwDebugEventCode==EXCEPTION_DEBUG_EVENT){
    auto& x=e.u.Exception;
    // The first breakpoint supplied by DebugActiveProcess completes the attach protocol.
    // Except for the strictly validated legacy thread-name protocol, pass
    // first-chance exceptions to the program; never claim fatal here.
    if(!ready&&x.dwFirstChance&&x.ExceptionRecord.ExceptionCode==EXCEPTION_BREAKPOINT){ready=true;}
    else if(threadNameNotification(process,e)){
     // Works at either chance; handle first chance so the target's top-level
     // filter need not run under a debugger. Keep armed, without fatal.claim.
     continuation=DBG_CONTINUE;
    }
    else {continuation=DBG_EXCEPTION_NOT_HANDLED;if(!x.dwFirstChance&&!completed){
     try {capture(process,e);} catch(...) {state("Failed","Dump",ERROR_UNHANDLED_EXCEPTION);}completed=true;
    }}
   }
   if(!ContinueDebugEvent(e.dwProcessId,e.dwThreadId,continuation)){if(!completed)state("Failed","Continue",GetLastError());break;}
   if(exiting){attached=false;if(!completed)state("Exited","Exit");break;}
  }
 } catch(DWORD error){if(!dir.empty())state("Failed","Connect",error);if(!ready)reply(pipe,error);}
 catch(...){if(!dir.empty())state("Failed","Helper",ERROR_UNHANDLED_EXCEPTION);if(!ready)reply(pipe,ERROR_UNHANDLED_EXCEPTION);}
 if(attached&&!DebugActiveProcessStop(pid)&&!completed)state("Failed","Detach",GetLastError());
 return 0;
}
