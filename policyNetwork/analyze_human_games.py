"""Analyze piece placement statistics from human games in the database."""
import json
import pymysql
from collections import defaultdict
from config import config


def connect_db():
    """Connect to MySQL database."""
    # Update these credentials as needed
    return pymysql.connect(
        host=config["MySQLServer"],
        user=config["MySQLUser"],
        password=config["MySQLPassword"],
        database='age_of_chess',
        cursorclass=pymysql.cursors.DictCursor
    )


def analyze_games():
    """Fetch and analyze 10x10 human games."""
    conn = connect_db()

    try:
        with conn.cursor() as cursor:
            # Fetch all 10x10 games
            cursor.execute("""
                SELECT *
                FROM HistoricGames
                WHERE BoardSize = 10
            """)

            games = cursor.fetchall()

            if not games:
                print("No 10x10 games found in database")
                return

            # Counters
            total_moves = 0
            total_placements = 0
            pawn_placements = 0
            knight_placements = 0
            bishop_placements = 0
            rook_placements = 0
            queen_placements = 0

            # Process each game
            for game in games:
                moves_json = game['MovesJson']
                if not moves_json:
                    continue

                moves = json.loads(moves_json)

                for move_notation in moves:
                    total_moves += 1

                    # Check if it's a placement (no dash/x, ends with piece code)
                    if '-' not in move_notation and 'x' not in move_notation:
                        # Last character is the piece code
                        piece = move_notation[-1].lower()

                        if piece in ['p', 'n', 'b', 'r', 'q']:
                            total_placements += 1

                            if piece == 'p':
                                pawn_placements += 1
                            elif piece == 'n':
                                knight_placements += 1
                            elif piece == 'b':
                                bishop_placements += 1
                            elif piece == 'r':
                                rook_placements += 1
                            elif piece == 'q':
                                queen_placements += 1

            # Calculate percentages
            if total_moves == 0:
                print("No moves found")
                return

            placement_pct = 100 * total_placements / total_moves
            pawn_pct = 100 * pawn_placements / total_moves
            knight_pct = 100 * knight_placements / total_moves
            bishop_pct = 100 * bishop_placements / total_moves
            rook_pct = 100 * rook_placements / total_moves
            queen_pct = 100 * queen_placements / total_moves

            # Print results
            print(f"Analyzed {len(games)} games with {total_moves} total moves")
            print(f"\nPlacement Statistics (% of all moves):")
            print(f"  Total placements: {placement_pct:.2f}%")
            print(f"  Pawns:   {pawn_pct:.2f}%")
            print(f"  Knights: {knight_pct:.2f}%")
            print(f"  Bishops: {bishop_pct:.2f}%")
            print(f"  Rooks:   {rook_pct:.2f}%")
            print(f"  Queens:  {queen_pct:.2f}%")

    finally:
        conn.close()


if __name__ == "__main__":
    analyze_games()
