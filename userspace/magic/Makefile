# Magic Language Tools Makefile
# Builds spc (Space Compiler) and spe (Space Emulator)

CC           = clang

# Default cross-compilation flags for standalone builds in this directory.
MAGIC_CFLAGS = -target aarch64-unknown-none-elf \
               -ffreestanding -fno-builtin \
               -nostdlib -nostdinc \
               -O2 -Wall -Wextra

# If CFLAGS is provided by the parent make (e.g. userspace/Makefile), keep it,
# otherwise fall back to our MAGIC_CFLAGS. Always ensure we can see libc/include.
CFLAGS      ?= $(MAGIC_CFLAGS)
CFLAGS      += -I../../libc/include \
               -Wno-unused-parameter

LD      = ld.lld
LDFLAGS = -nostdlib -static

BUILD_DIR = ../../build/userspace
LIBC_DIR  = ../../build/libc

CRT0 = $(LIBC_DIR)/crt0.o
LIBC = $(LIBC_DIR)/libc.a

# Common objects for magic runtime
MAGIC_OBJS = \
    $(BUILD_DIR)/magic_scanner.o \
    $(BUILD_DIR)/magic_compiler.o \
    $(BUILD_DIR)/magic_interpreter.o

.PHONY: all clean spc spe dirs

all: dirs spc spe

dirs:
	@mkdir -p $(BUILD_DIR)/bin

# ---- Magic runtime objects ----

$(BUILD_DIR)/magic_scanner.o: scanner.c magic.h
	@echo "[BUILD] magic/scanner.c"
	@$(CC) $(CFLAGS) -c scanner.c -o $@

$(BUILD_DIR)/magic_compiler.o: compiler.c magic.h
	@echo "[BUILD] magic/compiler.c"
	@$(CC) $(CFLAGS) -c compiler.c -o $@

$(BUILD_DIR)/magic_interpreter.o: interpreter.c magic.h
	@echo "[BUILD] magic/interpreter.c"
	@$(CC) $(CFLAGS) -c interpreter.c -o $@

# ---- spc: Space Compiler ----

$(BUILD_DIR)/spc_main.o: spc.c magic.h
	@echo "[BUILD] magic/spc.c"
	@$(CC) $(CFLAGS) -c spc.c -o $@

spc: dirs $(MAGIC_OBJS) $(BUILD_DIR)/spc_main.o
	@echo "[LINK] bin/spc"
	@$(LD) $(LDFLAGS) -o $(BUILD_DIR)/bin/spc \
	    $(CRT0) \
	    $(BUILD_DIR)/spc_main.o \
	    $(MAGIC_OBJS) \
	    $(LIBC)

# ---- spe: Space Emulator ----

$(BUILD_DIR)/spe_main.o: spe.c magic.h
	@echo "[BUILD] magic/spe.c"
	@$(CC) $(CFLAGS) -c spe.c -o $@

spe: dirs $(MAGIC_OBJS) $(BUILD_DIR)/spe_main.o
	@echo "[LINK] bin/spe"
	@$(LD) $(LDFLAGS) -o $(BUILD_DIR)/bin/spe \
	    $(CRT0) \
	    $(BUILD_DIR)/spe_main.o \
	    $(MAGIC_OBJS) \
	    $(LIBC)

clean:
	rm -f $(MAGIC_OBJS) \
	      $(BUILD_DIR)/spc_main.o \
	      $(BUILD_DIR)/spe_main.o \
	      $(BUILD_DIR)/bin/spc \
	      $(BUILD_DIR)/bin/spe

# ---- Host build (for testing on Linux/macOS) ----

.PHONY: host

HOST_CC = gcc
HOST_CFLAGS = -O2 -Wall -Wextra -Wno-unused-parameter

HOST_BUILD = /tmp/magic-host-build

host:
	@mkdir -p $(HOST_BUILD)
	@echo "[HOST] Building spc and spe for host..."
	@$(HOST_CC) $(HOST_CFLAGS) \
	    scanner.c compiler.c interpreter.c spc.c \
	    -o $(HOST_BUILD)/spc -lm
	@$(HOST_CC) $(HOST_CFLAGS) \
	    scanner.c compiler.c interpreter.c spe.c \
	    -o $(HOST_BUILD)/spe -lm
	@echo "[HOST] Built: $(HOST_BUILD)/spc, $(HOST_BUILD)/spe"

clean-host:
	rm -rf $(HOST_BUILD)
