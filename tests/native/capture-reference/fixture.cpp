// Compile the exact reviewed reference in this translation unit. Never call its
// debugger entrypoint, capture(), or any process-enumeration/dump function.
#define wmain CaptureReferenceEntryPoint
#include "capture-helper.cpp"
#undef wmain
#include <cstdio>

int main() {
    unsigned passed = 0;
    auto check = [&](bool value, const char* name) {
        std::printf("%s %s\n", value ? "PASS" : "FAIL", name);
        if (value) ++passed;
    };
    EXCEPTION_RECORD exception{};
    exception.ExceptionCode = EXCEPTION_ACCESS_VIOLATION;
    exception.NumberParameters = 2;
    check(memoryAccessException(exception), "access violation with two fields");
    exception.ExceptionCode = EXCEPTION_IN_PAGE_ERROR;
    check(memoryAccessException(exception), "in-page error with two fields");
    exception.NumberParameters = 1;
    check(!memoryAccessException(exception), "short in-page record rejected");
    exception.ExceptionCode = EXCEPTION_ACCESS_VIOLATION;
    check(!memoryAccessException(exception), "short access record rejected");
    exception.NumberParameters = 0;
    check(!memoryAccessException(exception), "empty access record rejected");
    exception.NumberParameters = 3;
    check(memoryAccessException(exception), "access record permits extra field");
    exception.ExceptionCode = 0x406D1388;
    check(!memoryAccessException(exception), "thread-name record has no access fields");
    exception.ExceptionCode = EXCEPTION_BREAKPOINT;
    check(!memoryAccessException(exception), "breakpoint has no access fields");
    check(safeName(L"helper-1_test.dll") == "helper-1_test.dll", "plain module basename");
    check(safeName(L"C:\\fixture/folder/module.dll") == "module.dll", "module paths removed");
    check(safeName(L"bad\"name.dll") == "UnknownModule", "JSON delimiter rejected");
    check(safeName(L"\u4e2d.dll") == "UnknownModule", "non-ASCII module name rejected");
    check(safeName(L"") == "UnknownModule", "empty module name rejected");
    check(safeName(std::wstring(129, L'a')) == "UnknownModule", "oversize module name rejected");
    std::vector<Module> modules{{0x1000, 0x20, "fixture.dll", ""}};
    check(location(0x101f, modules) == "{\"Module\":\"fixture.dll\",\"Rva\":\"0x1F\"}",
          "bounded module RVA");
    check(location(0x1020, modules) == "{\"Module\":\"UnknownModule\",\"Rva\":null}",
          "module end excluded");
    check(location(0x0fff, modules) == "{\"Module\":\"UnknownModule\",\"Rva\":null}",
          "address before module excluded");
    std::printf("MSVC_FULL_VER %d\n", _MSC_FULL_VER);
    std::printf("RESULT %u passed; 0 skipped\n", passed);
    return passed == 17 ? 0 : 1;
}
