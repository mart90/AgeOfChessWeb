import requests
import urllib3
from board import Board
from square import Square
from config import config

urllib3.disable_warnings(urllib3.exceptions.InsecureRequestWarning)

TERRAIN_MAP = {
    "DirtMine": "m",
    "GrassMine": "m",
    "DirtRocks": "r",
    "GrassRocks": "r",
    "DirtTrees": "t",
    "GrassTrees": "t",
}

SERVER_URL = "https://localhost:7074"


def parse_board(board_data, size):
    """Parse a single board JSON object into a Board instance."""
    squares = []
    for s in board_data["squares"]:
        sq = Square(s["x"] + s["y"] * size, s["x"], s["y"])
        sq.terrain_type = TERRAIN_MAP.get(s["type"])
        sq.has_treasure = s["hasTreasure"]
        if s["pieceType"] is not None:
            sq.piece_type = s["pieceType"]
            sq.piece_is_white = s["isWhite"]
        squares.append(sq)
    return Board(squares)


def fetch_boards(amount=100, size=10, server_url=SERVER_URL):
    """Fetch multiple mirrored boards in one API call."""
    resp = requests.post(
        f"{server_url}/api/sandbox/generateBulk",
        json={
            "Size": size,
            "IsRandom": False,
            "Amount": amount,
            "Token": config["GenerateBulkToken"],
        },
        verify=False,
    )
    resp.raise_for_status()

    boards = []
    for board_data in resp.json():
        boards.append(parse_board(board_data, size))
    return boards


if __name__ == "__main__":
    boards = fetch_boards(amount=5)
    print(f"Fetched {len(boards)} boards")
    for i, b in enumerate(boards):
        pieces = [s for s in b.squares if s.piece_type is not None]
        mines = [s for s in b.squares if s.terrain_type == "m"]
        treasures = [s for s in b.squares if s.has_treasure]
        print(f"  Board {i}: {len(pieces)} pieces, {len(mines)} mines, {len(treasures)} treasures")
