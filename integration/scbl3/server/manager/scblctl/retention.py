from __future__ import annotations


# Every SCBL-managed history uses one small, predictable retention window.
# Active references are always protected even if an inconsistent installation
# temporarily contains more than this number.
RETAINED_GENERATIONS = 3
