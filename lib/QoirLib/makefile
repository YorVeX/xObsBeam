# Build QoirLib as a shared library.
# - On Linux:   produces libQoirLib.so
# - On macOS:   produces libQoirLib.dylib
# The ARCH variable can be set to pass an explicit -arch flag (e.g. ARCH=arm64 or ARCH=x86_64)
# so that the universal build script can compile each architecture separately before combining
# them with lipo.

UNAME_S := $(shell uname -s)
ifeq ($(UNAME_S),Darwin)
  EXTENSION := .dylib
  OUTPUT := libQoirLib$(EXTENSION)
  # Apple clang does not support -static-libgcc / -static-libstdc++ the same way GCC does;
  # using -fvisibility=hidden by default and exporting only the DLL_EXPORT-marked symbols.
  ARCH_FLAG := $(if $(ARCH),-arch $(ARCH),)
  CXXFLAGS := -O3 -fvisibility=hidden $(ARCH_FLAG)
  LDFLAGS := -shared $(ARCH_FLAG)
else
  EXTENSION := .so
  OUTPUT := libQoirLib$(EXTENSION)
  CXXFLAGS := -O3 -static-libgcc -fvisibility=hidden -static-libstdc++
  LDFLAGS := -shared
endif

all: $(OUTPUT)

$(OUTPUT):
	$(CXX) $(CXXFLAGS) $(LDFLAGS) QoirLib.cpp -o $(OUTPUT)

clean:
	rm -f libQoirLib.so libQoirLib.dylib
