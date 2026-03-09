/*
 * UnixOS - Minimal C Library
 * Math functions (minimal subset, ASCII-safe)
 */

#include "../include/math.h"

double fabs(double x) {
    return x < 0 ? -x : x;
}

float fabsf(float x) {
    return x < 0.0f ? -x : x;
}
