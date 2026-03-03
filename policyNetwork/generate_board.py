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


def fetch_board(size=10, server_url=config["ServerUrl"]):
    resp = requests.post(
        f"{server_url}/api/sandbox/generateBulk",
        json={
            "Size": size,
            "IsRandom": False,
            "Amount": 1,
            "Token": config["GenerateBulkToken"],
        },
        verify=False,
    )
    resp.raise_for_status()
    board_data = resp.json()[0]

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


if __name__ == "__main__":
    board = fetch_board()
    print(f"Board size: {board.size}x{board.size}")
    print(f"Squares: {len(board.squares)}")
    pieces = board.piece_squares()
    print(f"Pieces: {len(pieces)}")
    for p in pieces:
        color = "White" if p.piece_is_white else "Black"
        print(f"  {color} {p.piece_type} at ({p.x}, {p.y})")
    mines = [s for s in board.squares if s.terrain_type == "m"]
    print(f"Mines: {len(mines)}")
    treasures = [s for s in board.squares if s.has_treasure]
    print(f"Treasures: {len(treasures)}")
