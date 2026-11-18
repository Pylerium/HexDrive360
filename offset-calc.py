import sys
from PyQt5.QtWidgets import (
    QApplication, QMainWindow, QWidget, QVBoxLayout, QHBoxLayout,
    QLabel, QLineEdit, QPushButton, QTableWidget, QTableWidgetItem,
    QMessageBox, QHeaderView, QComboBox
)
from PyQt5.QtCore import Qt

class OffsetTool(QMainWindow):
    def __init__(self):
        super().__init__()
        self.setWindowTitle("Pointer / Offset Manager – TU18")
        self.resize(900, 500)

        central = QWidget()
        self.setCentralWidget(central)
        layout = QVBoxLayout(central)

        # ===== Input Row =====
        input_layout = QHBoxLayout()

        self.name_input = QLineEdit()
        self.name_input.setPlaceholderText("Label (e.g. Max Ammo ZM)")

        self.base_input = QLineEdit()
        self.base_input.setPlaceholderText("Base Address (hex, e.g. 0x83551A10)")

        self.offset_input = QLineEdit()
        self.offset_input.setPlaceholderText("Offset (hex, e.g. 0x436)")

        self.value_input = QLineEdit()
        self.value_input.setPlaceholderText("Value (decimal or hex, optional)")

        self.type_input = QComboBox()
        self.type_input.addItems(["Int32", "Byte", "Float"])

        add_btn = QPushButton("Add Entry")
        add_btn.clicked.connect(self.add_entry)

        for w in [
            self.name_input, self.base_input, self.offset_input,
            self.value_input, self.type_input, add_btn
        ]:
            input_layout.addWidget(w)

        layout.addLayout(input_layout)

        # ===== Table =====
        self.table = QTableWidget(0, 6)
        self.table.setHorizontalHeaderLabels([
            "Name", "Base", "Offset", "Final Addr",
            "Value", "Poke Command"
        ])
        self.table.horizontalHeader().setSectionResizeMode(QHeaderView.Stretch)
        layout.addWidget(self.table)

        # ===== Resolve Row =====
        resolve_layout = QHBoxLayout()

        self.pointer_value_input = QLineEdit()
        self.pointer_value_input.setPlaceholderText(
            "Pointer value at base (hex, e.g. read *(0x83551A10))"
        )

        resolve_btn = QPushButton("Resolve Final Addresses")
        resolve_btn.clicked.connect(self.resolve_addresses)

        resolve_layout.addWidget(QLabel("Runtime Pointer Value:"))
        resolve_layout.addWidget(self.pointer_value_input)
        resolve_layout.addWidget(resolve_btn)

        layout.addLayout(resolve_layout)

    def add_entry(self):
        try:
            name = self.name_input.text().strip()
            base = int(self.base_input.text(), 16)
            offset = int(self.offset_input.text(), 16)
            value_text = self.value_input.text().strip()
            poke_type = self.type_input.currentText()

            value = value_text if value_text else ""

            row = self.table.rowCount()
            self.table.insertRow(row)

            self.table.setItem(row, 0, QTableWidgetItem(name))
            self.table.setItem(row, 1, QTableWidgetItem(hex(base)))
            self.table.setItem(row, 2, QTableWidgetItem(hex(offset)))
            self.table.setItem(row, 3, QTableWidgetItem("UNRESOLVED"))
            self.table.setItem(row, 4, QTableWidgetItem(value))
            self.table.setItem(row, 5, QTableWidgetItem(poke_type))

            self.name_input.clear()
            self.base_input.clear()
            self.offset_input.clear()
            self.value_input.clear()

        except Exception as e:
            QMessageBox.critical(self, "Error", f"Invalid input:\n{e}")

    def resolve_addresses(self):
        try:
            pointer_val = int(self.pointer_value_input.text(), 16)

            for row in range(self.table.rowCount()):
                offset = int(self.table.item(row, 2).text(), 16)
                final_addr = pointer_val + offset
                self.table.setItem(row, 3, QTableWidgetItem(hex(final_addr)))

                value = self.table.item(row, 4).text()
                poke_type = self.table.item(row, 5).text()

                if value:
                    if poke_type == "Int32":
                        cmd = f"PokeInt32({hex(final_addr)}, {int(value, 0)})"
                    elif poke_type == "Byte":
                        cmd = f"PokeByte({hex(final_addr)}, {int(value, 0)})"
                    else:
                        cmd = f"PokeFloat({hex(final_addr)}, {float(value)})"
                else:
                    cmd = "—"

                self.table.setItem(row, 5, QTableWidgetItem(cmd))

        except Exception as e:
            QMessageBox.critical(self, "Error", f"Failed to resolve:\n{e}")

if __name__ == "__main__":
    app = QApplication(sys.argv)
    win = OffsetTool()
    win.show()
    sys.exit(app.exec_())
