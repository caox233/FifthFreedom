// Diagnostic only: no filter/policy writes, admission decision or image startup.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#define _WIN32_WINNT 0x0A00
#include <windows.h>
#include <initguid.h>
#include <fwpmu.h>
#include <rpc.h>
#include <algorithm>
#include <array>
#include <chrono>
#include <cstring>
#include <iostream>
#include <string>
#include <string_view>

namespace {
constexpr UINT32 MaxConditions = 64, MaxFilters = 20000, PageSize = 128, MaxAppBytes = 65536;
constexpr unsigned ContractCount = 20;
struct Layer {
    const char* name; const GUID* key;
    UINT32 enumerated=0, exact_app=0, unsupported=0, block=0, permit=0, callout=0, other_action=0;
    UINT32 disabled=0, profile=0, interface_condition=0, other_condition=0, app_only=0;
    UINT32 clear_action_right=0, provider_context=0, weight_empty=0, weight_uint8=0, weight_uint64=0, weight_other=0;
    bool complete=false;
};
std::array<Layer,4> Layers() {
    return {{{"CONNECT_V4", &FWPM_LAYER_ALE_AUTH_CONNECT_V4}, {"CONNECT_V6", &FWPM_LAYER_ALE_AUTH_CONNECT_V6},
             {"RECV_V4", &FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V4}, {"RECV_V6", &FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V6}}};
}
bool Eq(const GUID& a, const GUID& b) { return !!InlineIsEqualGUID(a,b); }
bool Profile(const GUID& key) {
    return Eq(key,FWPM_CONDITION_CURRENT_PROFILE_ID) || Eq(key,FWPM_CONDITION_ORIGINAL_PROFILE_ID) ||
        Eq(key,FWPM_CONDITION_LOCAL_INTERFACE_PROFILE_ID) || Eq(key,FWPM_CONDITION_ARRIVAL_INTERFACE_PROFILE_ID) ||
        Eq(key,FWPM_CONDITION_NEXTHOP_INTERFACE_PROFILE_ID);
}
bool Interface(const GUID& key) {
    return Eq(key,FWPM_CONDITION_IP_LOCAL_INTERFACE) || Eq(key,FWPM_CONDITION_IP_ARRIVAL_INTERFACE) ||
        Eq(key,FWPM_CONDITION_IP_NEXTHOP_INTERFACE) || Eq(key,FWPM_CONDITION_INTERFACE_INDEX) ||
        Eq(key,FWPM_CONDITION_ARRIVAL_INTERFACE_INDEX) || Eq(key,FWPM_CONDITION_NEXTHOP_INTERFACE_INDEX) ||
        Eq(key,FWPM_CONDITION_INTERFACE_TYPE) || Eq(key,FWPM_CONDITION_TUNNEL_TYPE);
}
bool BlobValid(const FWP_BYTE_BLOB* value) {
    return value && value->size > 0 && value->size <= MaxAppBytes && value->data;
}
bool BlobEqual(const FWP_BYTE_BLOB& a, const FWP_BYTE_BLOB& b) {
    return a.size == b.size && std::memcmp(a.data,b.data,a.size) == 0;
}
// Only fixed classifications are retained. No display data, GUID, condition
// value, AppID bytes, weights, provider strings or filenames are serialized.
void Inspect(const FWPM_FILTER0& filter, const FWP_BYTE_BLOB& target, Layer& out) {
    ++out.enumerated;
    if (!BlobValid(&target) || !Eq(filter.layerKey,*out.key) || filter.numFilterConditions > MaxConditions ||
        (filter.numFilterConditions && !filter.filterCondition)) { ++out.unsupported; return; }
    unsigned appCount=0; bool exact=false, profile=false, interfaceCondition=false, other=false;
    for (UINT32 i=0;i<filter.numFilterConditions;++i) {
        const auto& condition=filter.filterCondition[i];
        if (Eq(condition.fieldKey,FWPM_CONDITION_ALE_APP_ID)) {
            if (++appCount > 1 || condition.conditionValue.type != FWP_BYTE_BLOB_TYPE ||
                !BlobValid(condition.conditionValue.byteBlob)) { ++out.unsupported; return; }
            exact=condition.matchType == FWP_MATCH_EQUAL && BlobEqual(*condition.conditionValue.byteBlob,target);
        } else if (Profile(condition.fieldKey)) { profile=true; }
        else if (Interface(condition.fieldKey)) { interfaceCondition=true; }
        else { other=true; }
    }
    if (!exact) { return; }
    ++out.exact_app;
    if (filter.action.type == FWP_ACTION_BLOCK) { ++out.block; }
    else if (filter.action.type == FWP_ACTION_PERMIT) { ++out.permit; }
    else if (filter.action.type & FWP_ACTION_FLAG_CALLOUT) { ++out.callout; }
    else { ++out.other_action; }
    if (filter.flags & FWPM_FILTER_FLAG_DISABLED) { ++out.disabled; }
    if (filter.flags & FWPM_FILTER_FLAG_CLEAR_ACTION_RIGHT) { ++out.clear_action_right; }
    if (filter.flags & FWPM_FILTER_FLAG_HAS_PROVIDER_CONTEXT) { ++out.provider_context; }
    if (profile) { ++out.profile; }
    if (interfaceCondition) { ++out.interface_condition; }
    if (other) { ++out.other_condition; }
    if (filter.numFilterConditions == 1) { ++out.app_only; }
    switch (filter.weight.type) {
        case FWP_EMPTY: ++out.weight_empty; break;
        case FWP_UINT8: ++out.weight_uint8; break;
        case FWP_UINT64: ++out.weight_uint64; break;
        default: ++out.weight_other; break;
    }
}
void PrintLayer(const Layer& value) {
    std::cout << "{\"name\":\"" << value.name << "\",\"complete\":" << (value.complete ? "true":"false");
#define FIELD(name,member) std::cout << ",\"" name "\":" << value.member
    FIELD("enumerated",enumerated); FIELD("exact_app",exact_app); FIELD("unsupported",unsupported);
    FIELD("block",block); FIELD("permit",permit); FIELD("callout",callout); FIELD("other_action",other_action);
    FIELD("disabled",disabled); FIELD("profile_conditions",profile); FIELD("interface_conditions",interface_condition);
    FIELD("other_conditions",other_condition); FIELD("app_only",app_only); FIELD("clear_action_right",clear_action_right);
    FIELD("provider_context",provider_context); FIELD("weight_empty",weight_empty); FIELD("weight_uint8",weight_uint8);
    FIELD("weight_uint64",weight_uint64); FIELD("weight_other",weight_other);
#undef FIELD
    std::cout << '}';
}
struct Report {
    std::array<Layer,4> layers=Layers(); const char* error=nullptr;
    bool read_only=false, transaction_closed=false;
    void Print() const {
        std::cout << "{\"schema\":1,\"kind\":\"wfp-exact-app-filter-diagnostic\",\"passed\":" << (error ? "false":"true")
            << ",\"coverage\":\"UNKNOWN\",\"arbitration_verified\":false,\"policy_changed\":false,\"admission_changed\":false"
            << ",\"observer_executed\":true,\"core_executed\":false,\"staging_image_executed\":false,\"driver_loaded\":false,\"raw_policy_published\":false"
            << ",\"read_only_transaction\":" << (read_only ? "true":"false")
            << ",\"transaction_closed\":" << (transaction_closed ? "true":"false") << ",\"error_code\":";
        if (error) { std::cout << '"' << error << '"'; } else { std::cout << "null"; }
        std::cout << ",\"layers\":[";
        for (size_t i=0;i<layers.size();++i) { if (i) { std::cout << ','; } PrintLayer(layers[i]); }
        std::cout << "]}\n";
    }
};
struct Engine {
    HANDLE value=nullptr; bool transaction=false;
    bool Close() {
        bool ok=true;
        if (transaction) { ok=FwpmTransactionAbort0(value) == ERROR_SUCCESS; transaction=false; }
        if (value) { ok=(FwpmEngineClose0(value) == ERROR_SUCCESS) && ok; value=nullptr; }
        return ok;
    }
    ~Engine() { Close(); }
};
struct Enumeration {
    HANDLE engine=nullptr, value=nullptr;
    bool Close() { if (!value) { return true; } const DWORD status=FwpmFilterDestroyEnumHandle0(engine,value); value=nullptr; return status == ERROR_SUCCESS; }
    ~Enumeration() { Close(); }
};
struct Batch {
    FWPM_FILTER0** rows=nullptr;
    ~Batch() { if (rows) { FwpmFreeMemory0(reinterpret_cast<void**>(&rows)); } }
};
struct AppId {
    FWP_BYTE_BLOB* value=nullptr;
    ~AppId() { if (value) { FwpmFreeMemory0(reinterpret_cast<void**>(&value)); } }
};
std::wstring Env(const wchar_t* name) {
    std::array<wchar_t,32768> value{};
    DWORD length=GetEnvironmentVariableW(name,value.data(),static_cast<DWORD>(value.size()));
    if (!length || length >= value.size()) { return {}; }
    return {value.data(),length};
}
bool Host() {
    auto image=Env(L"ImageOS");
    if (Env(L"GITHUB_ACTIONS") != L"true" || Env(L"RUNNER_ENVIRONMENT") != L"github-hosted" ||
        (image != L"win25" && image != L"win25-vs2026")) { return false; }
    HANDLE token=nullptr; TOKEN_ELEVATION elevation{}; DWORD bytes=0;
    if (!OpenProcessToken(GetCurrentProcess(),TOKEN_QUERY,&token)) { return false; }
    bool ok=GetTokenInformation(token,TokenElevation,&elevation,sizeof(elevation),&bytes) && elevation.TokenIsElevated;
    CloseHandle(token); return ok;
}
bool OwnedPath(std::wstring_view path) {
    std::wstring temp=Env(L"RUNNER_TEMP");
    while (!temp.empty() && temp.back() == L'\\') { temp.pop_back(); }
    std::wstring prefix=temp+L"\\ff-core-runtime-";
    constexpr std::wstring_view suffix=L"\\easytier-core.exe";
    if (temp.empty() || path.size() != prefix.size()+32+suffix.size() ||
        path.substr(0,prefix.size()) != prefix || path.substr(prefix.size()+32) != suffix) { return false; }
    for (wchar_t ch:path.substr(prefix.size(),32)) { if (!((ch>=L'0' && ch<=L'9') || (ch>=L'a' && ch<=L'f'))) { return false; } }
    const std::wstring full(path);
    DWORD attributes=GetFileAttributesW(full.c_str());
    if (attributes == INVALID_FILE_ATTRIBUTES || (attributes & (FILE_ATTRIBUTE_DIRECTORY|FILE_ATTRIBUTE_REPARSE_POINT))) { return false; }
    attributes=GetFileAttributesW(full.substr(0,prefix.size()+32).c_str());
    return attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_DIRECTORY) && !(attributes & FILE_ATTRIBUTE_REPARSE_POINT);
}
void Probe(const wchar_t* path, Report& report) {
    if (!Host()) { report.error="WFP_HOST_REQUIRED"; return; }
    if (!OwnedPath(path)) { report.error="WFP_INPUT_INVALID"; return; }
    AppId app;
    if (FwpmGetAppIdFromFileName0(path,&app.value) != ERROR_SUCCESS || !BlobValid(app.value)) { report.error="WFP_APP_ID_FAILED"; return; }
    Engine engine;
    if (FwpmEngineOpen0(nullptr,RPC_C_AUTHN_WINNT,nullptr,nullptr,&engine.value) != ERROR_SUCCESS) { report.error="WFP_ENGINE_FAILED"; return; }
    if (FwpmTransactionBegin0(engine.value,FWPM_TXN_READ_ONLY) != ERROR_SUCCESS) { report.error="WFP_TRANSACTION_FAILED"; return; }
    engine.transaction=true; report.read_only=true;
    const auto deadline=std::chrono::steady_clock::now()+std::chrono::seconds(15);
    UINT32 total=0;
    for (auto& layer:report.layers) {
        FWPM_FILTER_CONDITION0 condition{};
        condition.fieldKey=FWPM_CONDITION_ALE_APP_ID; condition.matchType=FWP_MATCH_EQUAL;
        condition.conditionValue.type=FWP_BYTE_BLOB_TYPE; condition.conditionValue.byteBlob=app.value;
        FWPM_FILTER_ENUM_TEMPLATE0 query{};
        query.layerKey=*layer.key; query.enumType=FWP_FILTER_ENUM_OVERLAPPING;
        query.flags=FWP_FILTER_ENUM_FLAG_INCLUDE_DISABLED; query.numFilterConditions=1;
        query.filterCondition=&condition; query.actionMask=0xFFFFFFFF;
        Enumeration enumeration; enumeration.engine=engine.value;
        if (FwpmFilterCreateEnumHandle0(engine.value,&query,&enumeration.value) != ERROR_SUCCESS) { report.error="WFP_ENUMERATION_FAILED"; break; }
        for (;;) {
            if (std::chrono::steady_clock::now() >= deadline) { report.error="WFP_ENUMERATION_LIMIT"; break; }
            Batch batch; UINT32 count=0;
            if (FwpmFilterEnum0(engine.value,enumeration.value,PageSize,&batch.rows,&count) != ERROR_SUCCESS) { report.error="WFP_ENUMERATION_FAILED"; break; }
            if (count > PageSize || (count && !batch.rows) || count > MaxFilters-total) { report.error="WFP_ENUMERATION_LIMIT"; break; }
            total+=count;
            for (UINT32 i=0;i<count;++i) {
                if (!batch.rows[i]) { report.error="WFP_ENUMERATION_FAILED"; break; }
                Inspect(*batch.rows[i],*app.value,layer);
            }
            if (report.error) { break; }
            if (count < PageSize) { layer.complete=true; break; }
        }
        if (!enumeration.Close() && !report.error) { report.error="WFP_CLEANUP_FAILED"; }
        if (report.error) { break; }
    }
    report.transaction_closed=engine.Close();
    if (!report.transaction_closed && !report.error) { report.error="WFP_CLEANUP_FAILED"; }
}
bool Contracts(unsigned& passed) {
    BYTE bytes[]={1,2,3,4}, changed[]={1,2,3,5}, longer[]={1,2,3,4,5};
    FWP_BYTE_BLOB target{4,bytes}, wrong{4,changed}, prefix{5,longer}, nullBlob{4,nullptr};
    auto test=[&](unsigned index) {
        auto layer=Layers()[0]; FWPM_FILTER_CONDITION0 conditions[3]{};
        conditions[0].fieldKey=FWPM_CONDITION_ALE_APP_ID; conditions[0].matchType=FWP_MATCH_EQUAL;
        conditions[0].conditionValue.type=FWP_BYTE_BLOB_TYPE; conditions[0].conditionValue.byteBlob=&target;
        FWPM_FILTER0 filter{}; filter.layerKey=*layer.key; filter.action.type=FWP_ACTION_BLOCK;
        filter.numFilterConditions=1; filter.filterCondition=conditions; filter.weight.type=FWP_EMPTY;
        switch (index) {
            case 1: conditions[0].conditionValue.byteBlob=&prefix; break;
            case 2: conditions[0].conditionValue.byteBlob=&wrong; break;
            case 3: filter.numFilterConditions=0; break;
            case 4: conditions[0].matchType=FWP_MATCH_NOT_EQUAL; break;
            case 5: conditions[0].conditionValue.type=FWP_UINT32; break;
            case 6: conditions[1]=conditions[0]; filter.numFilterConditions=2; break;
            case 7: conditions[1].fieldKey=FWPM_CONDITION_CURRENT_PROFILE_ID; filter.numFilterConditions=2; break;
            case 8: conditions[1].fieldKey=FWPM_CONDITION_IP_LOCAL_INTERFACE; filter.numFilterConditions=2; break;
            case 9: conditions[1].fieldKey=FWPM_CONDITION_IP_PROTOCOL; filter.numFilterConditions=2; break;
            case 10: filter.flags=FWPM_FILTER_FLAG_DISABLED; break;
            case 11: filter.action.type=FWP_ACTION_PERMIT; break;
            case 12: filter.action.type=FWP_ACTION_CALLOUT_TERMINATING; break;
            case 13: filter.action.type=FWP_ACTION_CONTINUE; break;
            case 14: filter.numFilterConditions=MaxConditions+1; break;
            case 15: filter.filterCondition=nullptr; break;
            case 16: conditions[0].conditionValue.byteBlob=&nullBlob; break;
            case 17: filter.layerKey=FWPM_LAYER_ALE_AUTH_CONNECT_V6; break;
            case 18: filter.weight.type=FWP_UINT64; break;
            case 19: filter.flags=FWPM_FILTER_FLAG_CLEAR_ACTION_RIGHT|FWPM_FILTER_FLAG_HAS_PROVIDER_CONTEXT; break;
            default: break;
        }
        Inspect(filter,target,layer);
        if (layer.enumerated != 1) { return false; }
        if (index>=1 && index<=4) { return layer.exact_app==0 && layer.unsupported==0; }
        if (index==5 || index==6 || (index>=14 && index<=17)) { return layer.exact_app==0 && layer.unsupported==1; }
        if (layer.exact_app!=1 || layer.unsupported) { return false; }
        switch (index) {
            case 7: return layer.profile==1 && layer.app_only==0;
            case 8: return layer.interface_condition==1 && layer.app_only==0;
            case 9: return layer.other_condition==1 && layer.app_only==0;
            case 10: return layer.disabled==1;
            case 11: return layer.permit==1 && layer.block==0;
            case 12: return layer.callout==1 && layer.block==0;
            case 13: return layer.other_action==1 && layer.block==0;
            case 18: return layer.weight_uint64==1 && layer.weight_empty==0;
            case 19: return layer.clear_action_right==1 && layer.provider_context==1;
            default: return layer.block==1 && layer.app_only==1 && layer.weight_empty==1;
        }
    };
    for (unsigned i=0;i<ContractCount;++i) { if (!test(i)) { return false; } ++passed; }
    return true;
}
} // namespace
int wmain(int argc,wchar_t** argv) {
    if (argc==2 && std::wstring_view(argv[1])==L"--contracts") {
        unsigned passed=0; bool ok=Contracts(passed);
        std::cout << "{\"schema\":1,\"kind\":\"wfp-filter-pure-contracts\",\"passed\":" << (ok ? "true":"false")
            << ",\"required_checks\":" << ContractCount << ",\"passed_checks\":" << passed
            << ",\"skipped_checks\":0,\"wfp_api_called\":false,\"policy_changed\":false,\"core_executed\":false}\n";
        return ok ? 0:1;
    }
    Report report;
    try {
        if (argc!=3 || std::wstring_view(argv[1])!=L"--app") { report.error="WFP_INPUT_INVALID"; }
        else { Probe(argv[2],report); }
    } catch (...) { report.error="WFP_OPERATION_FAILED"; }
    report.Print(); return report.error ? 1:0;
}
