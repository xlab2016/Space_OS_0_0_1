# Magic ASM - compiles on Linux (and anywhere with gcc/clang)
CC ?= gcc
CFLAGS ?= -O2 -Wall -Wextra
TARGET = magic-asm

.PHONY: all clean

all: $(TARGET)

$(TARGET): main.c
	$(CC) $(CFLAGS) -o $(TARGET) main.c

clean:
	rm -f $(TARGET)
