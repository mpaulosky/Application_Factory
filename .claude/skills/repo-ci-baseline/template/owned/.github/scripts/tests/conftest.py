import sys
from pathlib import Path

# The scripts under test live one directory up and aren't an installed package.
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
