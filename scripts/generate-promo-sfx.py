#!/usr/bin/env python3
"""Generate an original, music-free UI sound-effects track for the promo video."""

from array import array
import math
import random
import struct
import sys
import wave


SAMPLE_RATE = 48_000
DURATION = 32.0
TOTAL = int(SAMPLE_RATE * DURATION)
left = array("f", [0.0]) * TOTAL
right = array("f", [0.0]) * TOTAL
rng = random.Random(509)


def add_sample(index: int, value: float, pan: float = 0.0) -> None:
    if 0 <= index < TOTAL:
        left[index] += value * math.sqrt((1.0 - pan) * 0.5)
        right[index] += value * math.sqrt((1.0 + pan) * 0.5)


def add_chime(start: float, notes: tuple[float, ...], length: float = 0.7, gain: float = 0.18) -> None:
    start_sample = int(start * SAMPLE_RATE)
    count = int(length * SAMPLE_RATE)
    for i in range(count):
        t = i / SAMPLE_RATE
        attack = min(1.0, t / 0.018)
        envelope = attack * math.exp(-5.2 * t / length)
        value = 0.0
        for note_index, frequency in enumerate(notes):
            onset = note_index * 0.055
            if t >= onset:
                local_t = t - onset
                local_env = math.exp(-6.0 * local_t / max(0.1, length - onset))
                value += math.sin(2 * math.pi * frequency * local_t) * local_env
                value += 0.22 * math.sin(2 * math.pi * frequency * 2.01 * local_t) * local_env
        add_sample(start_sample + i, value * envelope * gain / max(1, len(notes)), pan=0.12)


def add_click(start: float, pitch: float = 820.0, pan: float = 0.0) -> None:
    start_sample = int(start * SAMPLE_RATE)
    count = int(0.11 * SAMPLE_RATE)
    for i in range(count):
        t = i / SAMPLE_RATE
        envelope = math.exp(-46 * t)
        transient = (rng.random() * 2 - 1) * math.exp(-110 * t)
        tone = math.sin(2 * math.pi * pitch * t) * envelope
        add_sample(start_sample + i, (tone * 0.13 + transient * 0.045), pan=pan)


def add_whoosh(start: float, length: float = 0.72, direction: float = 1.0) -> None:
    start_sample = int(start * SAMPLE_RATE)
    count = int(length * SAMPLE_RATE)
    smooth_noise = 0.0
    for i in range(count):
        t = i / SAMPLE_RATE
        progress = t / length
        envelope = math.sin(math.pi * progress) ** 1.8
        smooth_noise = smooth_noise * 0.965 + (rng.random() * 2 - 1) * 0.035
        frequency = 150 + 780 * progress
        tone = math.sin(2 * math.pi * frequency * t + 9 * progress * progress)
        value = (smooth_noise * 0.42 + tone * 0.055) * envelope
        pan = direction * (progress * 1.4 - 0.7)
        add_sample(start_sample + i, value, pan=max(-0.8, min(0.8, pan)))


def add_riser(start: float, length: float = 1.15) -> None:
    start_sample = int(start * SAMPLE_RATE)
    count = int(length * SAMPLE_RATE)
    phase = 0.0
    for i in range(count):
        t = i / SAMPLE_RATE
        progress = t / length
        frequency = 180 + 520 * progress * progress
        phase += 2 * math.pi * frequency / SAMPLE_RATE
        envelope = (progress ** 1.5) * (1 - 0.45 * progress)
        shimmer = math.sin(phase) + 0.25 * math.sin(phase * 2.013)
        add_sample(start_sample + i, shimmer * envelope * 0.055, pan=0.0)


# No continuous bed or background music: only scene-synchronized UI effects.
add_chime(0.28, (784.0, 1174.66, 1568.0), length=0.9, gain=0.22)
add_whoosh(3.95, 0.78, 1.0)
add_chime(4.34, (523.25, 783.99), length=0.62, gain=0.12)

add_click(9.18, 760, -0.35)
add_click(9.68, 860, 0.0)
add_click(10.18, 980, 0.35)

add_whoosh(12.78, 0.62, -1.0)
add_chime(13.18, (440.0, 659.25), length=0.55, gain=0.11)
add_click(14.28, 720, -0.15)
add_click(14.56, 920, 0.15)

add_whoosh(17.38, 0.78, 1.0)
add_click(18.80, 780, -0.3)
add_click(19.30, 850, 0.0)
add_click(19.80, 930, 0.3)

add_whoosh(21.98, 0.68, -1.0)
add_click(23.28, 880, -0.2)
add_click(23.78, 1040, 0.2)

add_riser(26.55, 1.20)
add_chime(27.52, (523.25, 783.99, 1046.50), length=1.15, gain=0.22)
add_click(28.35, 980, 0.0)


peak = max(max(abs(value) for value in left), max(abs(value) for value in right), 0.001)
scale = min(1.0, 0.78 / peak)

output = sys.argv[1] if len(sys.argv) > 1 else "promo-sfx.wav"
with wave.open(output, "wb") as wav:
    wav.setnchannels(2)
    wav.setsampwidth(2)
    wav.setframerate(SAMPLE_RATE)
    chunk = bytearray()
    for l_value, r_value in zip(left, right):
        l_sample = int(max(-1.0, min(1.0, l_value * scale)) * 32767)
        r_sample = int(max(-1.0, min(1.0, r_value * scale)) * 32767)
        chunk.extend(struct.pack("<hh", l_sample, r_sample))
    wav.writeframes(chunk)

print(output)
